using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace ModelContextProtocol.AspNet;

/// <summary>
/// Tracks stateful MCP sessions for the OWIN transport.
/// </summary>
public sealed class StatefulSessionManager
{
    private readonly ConcurrentDictionary<string, StreamableHttpSession> _sessions = new(StringComparer.Ordinal);

    internal bool TryGetValue(string key, [NotNullWhen(true)] out StreamableHttpSession? value) => _sessions.TryGetValue(key, out value);

    internal bool TryRemove(string key, [NotNullWhen(true)] out StreamableHttpSession? value) => _sessions.TryRemove(key, out value);

    internal ValueTask StartNewSessionAsync(StreamableHttpSession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryAdd(session.Id, session))
        {
            throw new InvalidOperationException($"Session with ID '{session.Id}' has already been created.");
        }

        return default;
    }
}
