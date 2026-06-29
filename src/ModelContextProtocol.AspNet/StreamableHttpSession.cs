using ModelContextProtocol.Server;

namespace ModelContextProtocol.AspNet;

internal sealed class StreamableHttpSession : IAsyncDisposable
{
    private readonly StatefulSessionManager _sessionManager;
    private readonly CancellationTokenSource _disposeCts = new();
    private int _started;

    public StreamableHttpSession(
        string sessionId,
        StreamableHttpServerTransport transport,
        McpServer server,
        StatefulSessionManager sessionManager)
    {
        Id = sessionId;
        Transport = transport;
        Server = server;
        _sessionManager = sessionManager;
    }

    public string Id { get; }
    public StreamableHttpServerTransport Transport { get; }
    public McpServer Server { get; }
    public CancellationToken SessionClosed => _disposeCts.Token;
    public Task ServerRunTask { get; set; } = Task.CompletedTask;

    public async ValueTask<IAsyncDisposable> AcquireReferenceAsync(CancellationToken cancellationToken)
    {
        if (Transport.Stateless)
        {
            return this;
        }

        if (!Transport.Stateless && Interlocked.Exchange(ref _started, 1) == 0)
        {
            await _sessionManager.StartNewSessionAsync(this, cancellationToken);
        }

        return new NoopAsyncDisposable();
    }

    public async ValueTask DisposeAsync()
    {
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
            await Server.DisposeAsync();
            _disposeCts.Dispose();
        }
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => default;
    }
}
