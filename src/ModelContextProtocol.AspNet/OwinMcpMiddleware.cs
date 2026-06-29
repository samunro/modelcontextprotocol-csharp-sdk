using Microsoft.Extensions.DependencyInjection;
using Owin;

namespace ModelContextProtocol.AspNet;

/// <summary>
/// OWIN middleware that exposes the Streamable HTTP handler at the root path.
/// </summary>
public sealed class OwinMcpMiddleware
{
    private readonly Func<IDictionary<string, object>, Task> _next;
    private readonly StreamableHttpHandler _handler;
    private readonly IServiceProvider _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="OwinMcpMiddleware"/> class.
    /// </summary>
    public OwinMcpMiddleware(Func<IDictionary<string, object>, Task> next, StreamableHttpHandler handler, IServiceProvider services)
    {
        _next = next;
        _handler = handler;
        _services = services;
    }

    /// <summary>
    /// Invokes the middleware for the current OWIN environment.
    /// </summary>
    public async Task Invoke(IDictionary<string, object> environment)
    {
        var requestPath = (string)environment["owin.RequestPath"]!;
        if (!string.Equals(requestPath, "/", StringComparison.Ordinal) && !string.Equals(requestPath, string.Empty, StringComparison.Ordinal))
        {
            await _next(environment);
            return;
        }

        using var scope = _services.CreateScope();
        environment["mcp.RequestServices"] = scope.ServiceProvider;

        var requestMethod = (string)environment["owin.RequestMethod"]!;
        var requestTask = requestMethod.ToUpperInvariant() switch
        {
            "POST" => _handler.HandlePostAsync(environment),
            "GET" => _handler.HandleGetAsync(environment),
            "DELETE" => _handler.HandleDeleteAsync(environment),
            _ => NotFoundAsync(environment),
        };
        await requestTask;
    }

    private static Task NotFoundAsync(IDictionary<string, object> environment)
    {
        environment["owin.ResponseStatusCode"] = 404;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Extension methods for registering the MCP OWIN middleware.
/// </summary>
public static class OwinMcpAppBuilderExtensions
{
    /// <summary>
    /// Adds the MCP middleware to the OWIN pipeline.
    /// </summary>
    public static IAppBuilder UseMcp(this IAppBuilder app, IServiceProvider services)
    {
        Throw.IfNull(app);
        Throw.IfNull(services);

        var handler = (StreamableHttpHandler)services.GetService(typeof(StreamableHttpHandler))!;
        if (handler is null)
        {
            throw new InvalidOperationException("You must call AddMcpServer().WithHttpTransport() before registering MCP OWIN middleware.");
        }

        return app.Use(typeof(OwinMcpMiddleware), handler, services);
    }
}
