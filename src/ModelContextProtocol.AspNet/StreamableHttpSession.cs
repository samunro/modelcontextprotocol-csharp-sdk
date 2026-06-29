using ModelContextProtocol.Server;

namespace ModelContextProtocol.AspNet;

internal sealed class StreamableHttpSession(
    string sessionId,
    StreamableHttpServerTransport transport,
    McpServer server,
    StatefulSessionManager sessionManager) : IAsyncDisposable
{
    private readonly CancellationTokenSource _disposeCts = new();
    private int _started;

    public string Id { get; } = sessionId;
    public StreamableHttpServerTransport Transport { get; } = transport;
    public McpServer Server { get; } = server;
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
            await sessionManager.StartNewSessionAsync(this, cancellationToken);
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
