using ModelContextProtocol.Server;

namespace ModelContextProtocol.AspNet;

internal sealed class StreamableHttpSession(
    string sessionId,
    StreamableHttpServerTransport transport,
    McpServer server,
    StatefulSessionManager sessionManager) : IAsyncDisposable
{
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _stateLock = new();
    private int _getRequestStarted;
    private int _referenceCount;
    private SessionState _state;

    public string Id { get; } = sessionId;
    public StreamableHttpServerTransport Transport { get; } = transport;
    public McpServer Server { get; } = server;
    private StatefulSessionManager SessionManager => sessionManager;
    public CancellationToken SessionClosed => _disposeCts.Token;
    public bool IsActive => !SessionClosed.IsCancellationRequested && Volatile.Read(ref _referenceCount) > 0;
    public long LastActivityTicks { get; private set; } = sessionManager.GetTimestamp();
    public Task ServerRunTask { get; set; } = Task.CompletedTask;

    public async ValueTask<IAsyncDisposable> AcquireReferenceAsync(CancellationToken cancellationToken)
    {
        if (Transport.Stateless)
        {
            return this;
        }

        SessionState startingState;
        lock (_stateLock)
        {
            startingState = _state;
            _referenceCount++;

            switch (startingState)
            {
                case SessionState.Uninitialized:
                    _state = SessionState.Started;
                    break;

                case SessionState.Started:
                    if (_referenceCount == 1)
                    {
                        sessionManager.DecrementIdleSessionCount();
                    }

                    LastActivityTicks = sessionManager.GetTimestamp();
                    break;

                case SessionState.Disposed:
                    _referenceCount--;
                    throw new ObjectDisposedException(nameof(StreamableHttpSession));
            }
        }

        if (startingState == SessionState.Uninitialized)
        {
            await sessionManager.StartNewSessionAsync(this, cancellationToken);
        }

        return new UnreferenceDisposable(this);
    }

    public bool TryStartGetRequest() => Interlocked.Exchange(ref _getRequestStarted, 1) == 0;

    public async ValueTask DisposeAsync()
    {
        var wasIdle = false;
        lock (_stateLock)
        {
            switch (_state)
            {
                case SessionState.Uninitialized:
                    break;

                case SessionState.Started:
                    wasIdle = _referenceCount == 0;
                    break;

                case SessionState.Disposed:
                    return;
            }

            _state = SessionState.Disposed;
        }

        try
        {
            await Transport.DisposeAsync();
            _disposeCts.Cancel();
            await ServerRunTask;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (wasIdle)
            {
                sessionManager.DecrementIdleSessionCount();
            }

            await Server.DisposeAsync();
            _disposeCts.Dispose();
        }
    }

    private sealed class UnreferenceDisposable(StreamableHttpSession session) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            lock (session._stateLock)
            {
                if (session._state != SessionState.Disposed && --session._referenceCount == 0)
                {
                    session.LastActivityTicks = session.SessionManager.GetTimestamp();
                    session.SessionManager.IncrementIdleSessionCount();
                }
            }

            return default;
        }
    }

    private enum SessionState
    {
        Uninitialized,
        Started,
        Disposed,
    }
}
