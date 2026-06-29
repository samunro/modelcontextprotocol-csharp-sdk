using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.AspNet;
using ModelContextProtocol.Server;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Provides methods for configuring OWIN-based MCP HTTP servers.
/// </summary>
public static class HttpMcpServerBuilderExtensions
{
    /// <summary>
    /// Adds the services required for the OWIN-based Streamable HTTP transport.
    /// </summary>
    public static IMcpServerBuilder WithHttpTransport(this IMcpServerBuilder builder, Action<HttpServerTransportOptions>? configureOptions = null)
    {
        Throw.IfNull(builder);

        builder.Services.TryAddSingleton<StatefulSessionManager>();
        builder.Services.TryAddSingleton<StreamableHttpHandler>();
        builder.Services.AddHostedService<IdleTrackingBackgroundService>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Transient<IConfigureOptions<HttpServerTransportOptions>, HttpServerTransportOptionsSetup>());

        if (configureOptions is not null)
        {
            builder.Services.Configure(configureOptions);
        }

        return builder;
    }
}
