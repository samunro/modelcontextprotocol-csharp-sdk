using Microsoft.Extensions.DependencyInjection;
using Microsoft.Owin.Builder;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Owin;
using System.Net.Http;
using System.Text;
using Xunit;

namespace ModelContextProtocol.AspNet.Tests;

public class OwinTransportIntegrationTests
{
    [Fact]
    public async Task HandlePostAsync_WritesJsonErrorForInvalidRequest()
    {
        var services = CreateServices();
        await using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<StreamableHttpHandler>();

        var responseHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var responseBody = new MemoryStream();
        var requestBody = new MemoryStream("not-json"u8.ToArray());
        var requestHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = new[] { "application/json, text/event-stream" },
            ["Content-Type"] = new[] { "application/json" },
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

        await handler.HandlePostAsync(environment);

        responseBody.Position = 0;
        var payload = await new StreamReader(responseBody, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true)
            .ReadToEndAsync();

        Assert.Contains("valid JSON-RPC message", payload);
        Assert.Equal(400, environment["owin.ResponseStatusCode"]);
        Assert.Equal("application/json", responseHeaders["Content-Type"][0]);
    }

    [Fact]
    public async Task HandlePostAsync_RejectsMissingJsonContentType()
    {
        var services = CreateServices();
        await using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<StreamableHttpHandler>();

        var environment = CreateOwinEnvironment(
            provider,
            "POST",
            "{}",
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Accept"] = new[] { "application/json, text/event-stream" },
            });

        await handler.HandlePostAsync(environment);

        Assert.Equal(415, environment["owin.ResponseStatusCode"]);
    }

    [Fact]
    public async Task HandlePostAsync_RejectsMcpSessionIdHeader()
    {
        var services = CreateServices();
        await using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<StreamableHttpHandler>();

        var headers = JsonPostHeaders();
        headers["mcp-session-id"] = new[] { "abc" };
        var environment = CreateOwinEnvironment(
            provider,
            "POST",
            ListToolsRequest,
            headers);

        await handler.HandlePostAsync(environment);

        Assert.Equal(400, environment["owin.ResponseStatusCode"]);
    }

    [Fact]
    public async Task HandleGetAsync_ReturnsMethodNotAllowed()
    {
        var services = CreateServices();
        await using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<StreamableHttpHandler>();

        var environment = CreateOwinEnvironment(
            provider,
            "GET",
            string.Empty,
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Accept"] = new[] { "text/event-stream" },
            });

        await handler.HandleGetAsync(environment);

        Assert.Equal(405, environment["owin.ResponseStatusCode"]);
    }

    [Fact]
    public async Task OwinMiddleware_UsesRequestScopeForMcpRequestServices()
    {
        ScopedProbe? resolvedProbe = null;
        var services = CreateServices(configureTransport: options =>
        {
            options.ConfigureSessionOptions = (environment, _, _) =>
            {
                var requestServices = (IServiceProvider)environment["mcp.RequestServices"];
                resolvedProbe = requestServices.GetRequiredService<ScopedProbe>();
                return Task.CompletedTask;
            };
        });
        services.AddScoped<ScopedProbe>();

        await using var provider = services.BuildServiceProvider();
        var app = new AppBuilder();
        app.UseMcp(provider);
        var middleware = (Func<IDictionary<string, object>, Task>)app.Build(typeof(Func<IDictionary<string, object>, Task>));

        var environment = CreateOwinEnvironment(provider, "POST", InitializeRequest, JsonPostHeaders());

        await middleware(environment);

        Assert.NotNull(resolvedProbe);
        Assert.True(resolvedProbe.Disposed);
    }

    [Fact]
    public async Task OwinEndpoint_ConnectsMcpClientAndCallsTool()
    {
        var services = CreateServices();
        await using var provider = services.BuildServiceProvider();
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
    public async Task OwinEndpoint_DoesNotReturnSessionId()
    {
        var services = CreateServices();
        await using var provider = services.BuildServiceProvider();
        var httpClient = new HttpClient(new OwinMcpHttpMessageHandler(provider))
        {
            BaseAddress = new Uri("http://localhost/"),
        };

        await using var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/"),
                Name = "OWIN stateless integration client",
                TransportMode = HttpTransportMode.StreamableHttp,
            }, httpClient, ownsHttpClient: true),
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "OWIN stateless integration test", Version = "1.0.0" },
                ProtocolVersion = "2025-11-25",
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(string.IsNullOrEmpty(client.SessionId));

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(tools, tool => tool.Name == "echo");
    }

    private static ServiceCollection CreateServices(
        Action<HttpServerTransportOptions>? configureTransport = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "OwinIntegrationServer", Version = "1.0.0" };
            })
            .WithHttpTransport(options =>
            {
                configureTransport?.Invoke(options);
            })
            .WithTools([McpServerTool.Create((string text) => $"Echo: {text}", new() { Name = "echo" })]);
        return services;
    }

    private const string InitializeRequest = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"1.0.0"}}}
        """;

    private const string ListToolsRequest = """
        {"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}
        """;

    private static Dictionary<string, string[]> JsonPostHeaders() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = new[] { "application/json, text/event-stream" },
            ["Content-Type"] = new[] { "application/json" },
        };

    private static Dictionary<string, object> CreateOwinEnvironment(
        IServiceProvider services,
        string method,
        string body,
        IDictionary<string, string[]> requestHeaders,
        object? user = null)
    {
        var environment = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["owin.RequestMethod"] = method,
            ["owin.RequestPath"] = "/",
            ["owin.RequestBody"] = new MemoryStream(Encoding.UTF8.GetBytes(body)),
            ["owin.RequestHeaders"] = requestHeaders,
            ["owin.ResponseHeaders"] = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase),
            ["owin.ResponseBody"] = new MemoryStream(),
            ["mcp.RequestServices"] = services,
        };

        if (user is not null)
        {
            environment["server.User"] = user;
        }

        return environment;
    }

    private sealed class ScopedProbe : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class OwinMcpHttpMessageHandler(IServiceProvider services) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var handler = services.GetRequiredService<StreamableHttpHandler>();
            var requestBody = new MemoryStream();
            if (request.Content is not null)
            {
                await request.Content.CopyToAsync(requestBody);
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
                ["mcp.RequestServices"] = services,
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
