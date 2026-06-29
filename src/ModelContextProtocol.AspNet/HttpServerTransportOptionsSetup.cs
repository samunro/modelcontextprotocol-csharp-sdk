using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace ModelContextProtocol.AspNet;

internal sealed class HttpServerTransportOptionsSetup : IConfigureOptions<HttpServerTransportOptions>
{
    public void Configure(HttpServerTransportOptions options)
    {
    }
}
