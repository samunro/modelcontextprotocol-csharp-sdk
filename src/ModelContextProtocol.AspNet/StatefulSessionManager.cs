using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ModelContextProtocol.AspNet;

/// <summary>
/// Tracks stateful MCP sessions for the OWIN transport.
/// </summary>
public sealed class StatefulSessionManager : IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, StreamableHttpSession> _sessions = new(StringComparer.Ordinal);
    private readonly ILogger<StatefulSessionManager> _logger;
    private readonly TimeSpan _idleTimeout;
    private readonly int _maxIdleSessionCount;
    private readonly Timer? _pruningTimer;
    private readonly SemaphoreSlim _pruningLock = new(1, 1);
    private long _currentIdleSessionCount;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="StatefulSessionManager"/> class.
    /// </summary>
    public StatefulSessionManager(IOptions<HttpServerTransportOptions> options, ILogger<StatefulSessionManager> logger)
    {
        Throw.IfNull(options);
        Throw.IfNull(logger);

        var value = options.Value;
        if (value.IdleTimeout != Timeout.InfiniteTimeSpan && value.IdleTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(HttpServerTransportOptions.IdleTimeout));
        }

        if (value.MaxIdleSessionCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(HttpServerTransportOptions.MaxIdleSessionCount));
        }

        _logger = logger;
        _idleTimeout = value.IdleTimeout;
        _maxIdleSessionCount = value.MaxIdleSessionCount;

        if (!value.Stateless)
        {
            _pruningTimer = new Timer(PruneIdleSessions, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
    }

    internal bool TryGetValue(string key, [NotNullWhen(true)] out StreamableHttpSession? value) => _sessions.TryGetValue(key, out value);

    internal bool TryRemove(string key, [NotNullWhen(true)] out StreamableHttpSession? value) => _sessions.TryRemove(key, out value);

    internal long GetTimestamp() => DateTimeOffset.UtcNow.UtcTicks;

    internal void IncrementIdleSessionCount() => Interlocked.Increment(ref _currentIdleSessionCount);

    internal void DecrementIdleSessionCount() => Interlocked.Decrement(ref _currentIdleSessionCount);

    internal async ValueTask StartNewSessionAsync(StreamableHttpSession session, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (!TryAddSessionImmediately(session))
            {
                var sessionToPrune = FindLongestIdleSession();
                if (sessionToPrune is null)
                {
                    _logger.LogCritical(
                        "MaxIdleSessionCount of {MaxIdleSessionCount} exceeded, and {CurrentIdleSessionCount} sessions are currently active or closing. Creating new session {SessionId} anyway.",
                        _maxIdleSessionCount,
                        Volatile.Read(ref _currentIdleSessionCount),
                        session.Id);
                    AddSession(session);
                    return;
                }

                _logger.LogInformation(
                    "MaxIdleSessionCount of {MaxIdleSessionCount} exceeded. Closing idle session {SessionId} despite it being active more recently than the configured IdleTimeout to make room for new sessions.",
                    _maxIdleSessionCount,
                    sessionToPrune.Id);

                await DisposeSessionAsync(sessionToPrune);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    internal async Task PruneIdleSessionsAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        if (!await _pruningLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var idleActivityCutoff = _idleTimeout == Timeout.InfiniteTimeSpan
                ? long.MinValue
                : GetTimestamp() - _idleTimeout.Ticks;

            foreach (var session in _sessions.Values.OrderBy(static session => session.LastActivityTicks).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (session.IsActive || session.SessionClosed.IsCancellationRequested)
                {
                    continue;
                }

                if (session.LastActivityTicks < idleActivityCutoff)
                {
                    _logger.LogInformation(
                        "IdleTimeout of {IdleTimeout} exceeded. Closing idle session {SessionId}.",
                        _idleTimeout,
                        session.Id);
                    await RemoveAndCloseSessionAsync(session.Id);
                }
            }

            while (Volatile.Read(ref _currentIdleSessionCount) > _maxIdleSessionCount)
            {
                var sessionToPrune = FindLongestIdleSession();
                if (sessionToPrune is null)
                {
                    return;
                }

                _logger.LogInformation(
                    "MaxIdleSessionCount of {MaxIdleSessionCount} exceeded. Closing idle session {SessionId} despite it being active more recently than the configured IdleTimeout to make room for new sessions.",
                    _maxIdleSessionCount,
                    sessionToPrune.Id);
                await DisposeSessionAsync(sessionToPrune);
            }
        }
        finally
        {
            _pruningLock.Release();
        }
    }

    internal async Task DisposeAllSessionsAsync()
    {
        var disposeSessionTasks = new List<Task>();

        foreach (var sessionKey in _sessions.Keys)
        {
            if (_sessions.TryRemove(sessionKey, out var session))
            {
                disposeSessionTasks.Add(DisposeSessionAsync(session));
            }
        }

        await Task.WhenAll(disposeSessionTasks);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pruningTimer?.Dispose();
        DisposeAllSessionsAsync().GetAwaiter().GetResult();
        _pruningLock.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pruningTimer?.Dispose();
        await DisposeAllSessionsAsync();
        _pruningLock.Dispose();
    }

    private bool TryAddSessionImmediately(StreamableHttpSession session)
    {
        if (Volatile.Read(ref _currentIdleSessionCount) < _maxIdleSessionCount)
        {
            AddSession(session);
            return true;
        }

        return false;
    }

    private void AddSession(StreamableHttpSession session)
    {
        if (!_sessions.TryAdd(session.Id, session))
        {
            throw new InvalidOperationException($"Session with ID '{session.Id}' has already been created.");
        }
    }

    private StreamableHttpSession? FindLongestIdleSession()
    {
        foreach (var session in _sessions.Values.OrderBy(static session => session.LastActivityTicks))
        {
            if (!session.IsActive &&
                !session.SessionClosed.IsCancellationRequested &&
                _sessions.TryRemove(session.Id, out var removedSession))
            {
                return removedSession;
            }
        }

        return null;
    }

    private async Task RemoveAndCloseSessionAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            await DisposeSessionAsync(session);
        }
    }

    private async Task DisposeSessionAsync(StreamableHttpSession session)
    {
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing session {SessionId}.", session.Id);
        }
    }

    private void PruneIdleSessions(object? state)
    {
        if (!_disposed)
        {
            _ = PruneIdleSessionsAsync(CancellationToken.None);
        }
    }
}
