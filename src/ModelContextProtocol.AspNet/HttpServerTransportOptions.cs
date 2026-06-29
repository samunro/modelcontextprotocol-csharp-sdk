using ModelContextProtocol.Server;

namespace ModelContextProtocol.AspNet;

/// <summary>
/// Minimal configuration surface for the OWIN-based Streamable HTTP transport.
/// </summary>
public class HttpServerTransportOptions
{
    /// <summary>
    /// Gets or sets a value that indicates whether the server runs in stateless mode.
    /// </summary>
    public bool Stateless { get; set; } = true;

    /// <summary>
    /// Gets or sets the duration of time the server will wait between active requests before timing out a stateful MCP session.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromHours(2);

    /// <summary>
    /// Gets or sets the maximum number of idle stateful sessions to keep in memory.
    /// </summary>
    public int MaxIdleSessionCount { get; set; } = 10_000;

    /// <summary>
    /// Gets or sets an optional callback used to configure per-session server options.
    /// </summary>
    public Func<IDictionary<string, object>, McpServerOptions, CancellationToken, Task>? ConfigureSessionOptions { get; set; }

    /// <summary>
    /// Gets or sets an optional callback used to run a session.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.Experimental(Experimentals.RunSessionHandler_DiagnosticId, UrlFormat = Experimentals.RunSessionHandler_Url)]
    public Func<IDictionary<string, object>, McpServer, CancellationToken, Task>? RunSessionHandler { get; set; }
}
