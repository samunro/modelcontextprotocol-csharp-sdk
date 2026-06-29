using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Text;
using Xunit;

namespace ModelContextProtocol.AspNet.Tests;

public class OwinTransportIntegrationTests
{
    [Fact]
    public async Task HandlePostAsync_WritesJsonErrorForInvalidRequest()
    {
        var services = CreateServices();
        using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<StreamableHttpHandler>();

        var responseHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var responseBody = new MemoryStream();
        var requestBody = new MemoryStream(Encoding.UTF8.GetBytes("not-json"));
        var requestHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = new[] { "application/json, text/event-stream" },
        };
        var environment = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["owin.RequestMethod"] = "POST",
            ["owin.RequestPath"] = "/",
            ["owin.RequestBody"] = requestBody,
            ["owin.RequestHeaders"] = requestHeaders,
            ["owin.ResponseHeaders"] = responseHeaders,
            ["owin.ResponseBody"] = responseBody,
            ["mcp.RequestServices"] = provider,
        };

        await handler.HandlePostAsync(environment).WaitAsync(TestContext.Current.CancellationToken);

        responseBody.Position = 0;
        var payload = await new StreamReader(responseBody, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true)
            .ReadToEndAsync(TestContext.Current.CancellationToken);

        Assert.Contains("valid JSON-RPC message", payload);
        Assert.Equal(400, environment["owin.ResponseStatusCode"]);
        Assert.Equal("application/json", responseHeaders["Content-Type"][0]);
    }

    [Fact]
    public async Task OwinEndpoint_ConnectsMcpClientAndCallsTool()
    {
        var services = CreateServices();
        using var provider = services.BuildServiceProvider();
        var httpClient = new HttpClient(new OwinMcpHttpMessageHandler(provider))
        {
            BaseAddress = new Uri("http://localhost/"),
        };

        await using var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/"),
                Name = "OWIN integration client",
                TransportMode = HttpTransportMode.StreamableHttp,
            }, httpClient, ownsHttpClient: true),
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "OWIN integration test", Version = "1.0.0" },
                ProtocolVersion = "2025-11-25",
            },
            cancellationToken: TestContext.Current.CancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(tools, tool => tool.Name == "echo");

        var result = await client.CallToolAsync(
            "echo",
            new Dictionary<string, object?> { ["text"] = "hello" },
            cancellationToken: TestContext.Current.CancellationToken);

        var text = Assert.Single(result.Content.OfType<TextContentBlock>());
        Assert.Equal("Echo: hello", text.Text);
    }

    [Fact]
    public async Task OwinEndpoint_StatefulMode_ReusesSession()
    {
        var services = CreateServices(stateless: false);
        using var provider = services.BuildServiceProvider();
        var httpClient = new HttpClient(new OwinMcpHttpMessageHandler(provider))
        {
            BaseAddress = new Uri("http://localhost/"),
        };

        await using var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/"),
                Name = "OWIN stateful integration client",
                TransportMode = HttpTransportMode.StreamableHttp,
            }, httpClient, ownsHttpClient: true),
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "OWIN stateful integration test", Version = "1.0.0" },
                ProtocolVersion = "2025-11-25",
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrEmpty(client.SessionId));

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(tools, tool => tool.Name == "echo");
    }

    private static ServiceCollection CreateServices(bool stateless = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "OwinIntegrationServer", Version = "1.0.0" };
            })
            .WithHttpTransport(options => options.Stateless = stateless)
            .WithTools([McpServerTool.Create((string text) => $"Echo: {text}", new() { Name = "echo" })]);
        return services;
    }

    private sealed class OwinMcpHttpMessageHandler : HttpMessageHandler
    {
        private readonly IServiceProvider _services;

        public OwinMcpHttpMessageHandler(IServiceProvider services)
        {
            _services = services;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var handler = _services.GetRequiredService<StreamableHttpHandler>();
            var requestBody = new MemoryStream();
            if (request.Content is not null)
            {
                await request.Content.CopyToAsync(requestBody, cancellationToken);
                requestBody.Position = 0;
            }

            var responseBody = new MemoryStream();
            var requestHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers)
            {
                requestHeaders[header.Key] = header.Value.ToArray();
            }

            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                {
                    requestHeaders[header.Key] = header.Value.ToArray();
                }
            }

            var responseHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            var environment = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["owin.RequestMethod"] = request.Method.Method,
                ["owin.RequestPath"] = request.RequestUri?.AbsolutePath ?? "/",
                ["owin.RequestBody"] = requestBody,
                ["owin.RequestHeaders"] = requestHeaders,
                ["owin.ResponseHeaders"] = responseHeaders,
                ["owin.ResponseBody"] = responseBody,
                ["owin.CallCancelled"] = cancellationToken,
                ["mcp.RequestServices"] = _services,
            };

            switch (request.Method.Method.ToUpperInvariant())
            {
                case "POST":
                    await handler.HandlePostAsync(environment);
                    break;
                case "GET":
                    await handler.HandleGetAsync(environment);
                    break;
                case "DELETE":
                    await handler.HandleDeleteAsync(environment);
                    break;
                default:
                    environment["owin.ResponseStatusCode"] = 404;
                    break;
            }

            responseBody.Position = 0;
            var response = new HttpResponseMessage((System.Net.HttpStatusCode)(environment.TryGetValue("owin.ResponseStatusCode", out var statusCode) ? (int)statusCode : 200))
            {
                Content = new ByteArrayContent(responseBody.ToArray()),
                RequestMessage = request,
            };

            foreach (var header in responseHeaders)
            {
                if (!response.Headers.TryAddWithoutValidation(header.Key, header.Value))
                {
                    response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return response;
        }
    }
}
