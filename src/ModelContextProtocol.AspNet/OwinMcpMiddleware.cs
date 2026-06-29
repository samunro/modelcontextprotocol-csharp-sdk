using ModelContextProtocol;
using Owin;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ModelContextProtocol.AspNet;

/// <summary>
/// OWIN middleware that exposes the Streamable HTTP handler at the root path.
/// </summary>
/// <summary>
/// OWIN middleware that routes root POST requests to the MCP streamable HTTP handler.
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
    public Task Invoke(IDictionary<string, object> environment)
    {
        var requestPath = (string)environment["owin.RequestPath"]!;
        if (!string.Equals(requestPath, "/", StringComparison.Ordinal) && !string.Equals(requestPath, string.Empty, StringComparison.Ordinal))
        {
            return _next(environment);
        }

        environment["mcp.RequestServices"] = _services;

        var requestMethod = (string)environment["owin.RequestMethod"]!;
        return requestMethod.ToUpperInvariant() switch
        {
            "POST" => _handler.HandlePostAsync(environment),
            "GET" => _handler.HandleGetAsync(environment),
            "DELETE" => _handler.HandleDeleteAsync(environment),
            _ => NotFoundAsync(environment),
        };
    }

    private static Task NotFoundAsync(IDictionary<string, object> environment)
    {
        environment["owin.ResponseStatusCode"] = 404;
        return Task.CompletedTask;
    }
}

/// <summary>
/// OWIN app builder extensions for registering the MCP middleware.
/// </summary>
/// <summary>
/// Extension methods for registering the MCP OWIN middleware.
/// </summary>
public static class OwinMcpAppBuilderExtensions
{
    /// <summary>
    /// Adds the MCP middleware to the OWIN pipeline.
    /// </summary>
    public static IAppBuilder UseMcp(this IAppBuilder app, StreamableHttpHandler handler, IServiceProvider services)
    {
        Throw.IfNull(app);
        Throw.IfNull(handler);
        Throw.IfNull(services);

        return app.Use(typeof(OwinMcpMiddleware), handler, services);
    }

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

        return app.UseMcp(handler, services);
    }
}
