using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ModelContextProtocol.AspNet;

/// <summary>
/// Handles stateless Streamable HTTP requests for an OWIN-based MCP server.
/// </summary>
public sealed class StreamableHttpHandler
{
    private const string McpSessionIdHeaderName = McpHttpHeaders.SessionId;
    private const string McpProtocolVersionHeaderName = McpHttpHeaders.ProtocolVersion;

    private static readonly HashSet<string> SupportedProtocolVersions = new(StringComparer.Ordinal)
    {
        "2024-11-05",
        "2025-03-26",
        "2025-06-18",
        McpHttpHeaders.November2025ProtocolVersion,
        McpHttpHeaders.July2026ProtocolVersion,
    };

    private static readonly JsonTypeInfo<JsonRpcMessage> MessageTypeInfo = GetRequiredJsonTypeInfo<JsonRpcMessage>();
    private static readonly JsonTypeInfo<JsonRpcError> ErrorTypeInfo = GetRequiredJsonTypeInfo<JsonRpcError>();
    private static readonly JsonTypeInfo<UnsupportedProtocolVersionErrorData> UnsupportedProtocolVersionTypeInfo =
        GetRequiredJsonTypeInfo<UnsupportedProtocolVersionErrorData>();

    private readonly IOptions<McpServerOptions> _mcpServerOptionsSnapshot;
    private readonly IOptionsFactory<McpServerOptions> _mcpServerOptionsFactory;
    private readonly IOptions<HttpServerTransportOptions> _httpServerTransportOptions;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamableHttpHandler"/> class.
    /// </summary>
    public StreamableHttpHandler(
        IOptions<McpServerOptions> mcpServerOptionsSnapshot,
        IOptionsFactory<McpServerOptions> mcpServerOptionsFactory,
        IOptions<HttpServerTransportOptions> httpServerTransportOptions,
        ILoggerFactory loggerFactory)
    {
        _mcpServerOptionsSnapshot = mcpServerOptionsSnapshot;
        _mcpServerOptionsFactory = mcpServerOptionsFactory;
        _httpServerTransportOptions = httpServerTransportOptions;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Gets the configured HTTP transport options.
    /// </summary>
    public HttpServerTransportOptions HttpServerTransportOptions => _httpServerTransportOptions.Value;

    /// <summary>
    /// Handles an incoming POST request from the OWIN environment.
    /// </summary>
    public async Task HandlePostAsync(IDictionary<string, object> environment)
    {
        var context = new OwinMcpContext(environment);

        if (!HasJsonContentType(context))
        {
            context.StatusCode = 415;
            return;
        }

        if (!ValidateProtocolVersionHeader(context, out var protocolVersionError))
        {
            await WriteJsonRpcErrorDetailAsync(context, protocolVersionError, 400);
            return;
        }

        if (!ClientAccepts(context, "application/json") || !ClientAccepts(context, "text/event-stream"))
        {
            await WriteJsonRpcErrorAsync(
                context,
                "Not Acceptable: Client must accept both application/json and text/event-stream",
                406);
            return;
        }

        if (!string.IsNullOrEmpty(context.GetRequestHeader(McpSessionIdHeaderName)))
        {
            await WriteJsonRpcErrorAsync(context, "Bad Request: The Mcp-Session-Id header is not supported", 400);
            return;
        }

        JsonRpcMessage? message;
        try
        {
            message = await ReadJsonRpcMessageAsync(context);
        }
        catch (JsonException)
        {
            await WriteJsonRpcErrorAsync(
                context,
                "Bad Request: The POST body did not contain a valid JSON-RPC message.",
                400,
                (int)McpErrorCode.InvalidRequest);
            return;
        }

        if (message is null)
        {
            await WriteJsonRpcErrorAsync(
                context,
                "Bad Request: The POST body did not contain a valid JSON-RPC message.",
                400,
                (int)McpErrorCode.InvalidRequest);
            return;
        }

        var toolCollection = _mcpServerOptionsSnapshot.Value.ToolCollection;
        if (!ValidateMcpHeaders(context, message, toolCollection, out var errorMessage))
        {
            await WriteJsonRpcErrorAsync(context, errorMessage, 400, (int)McpErrorCode.HeaderMismatch);
            return;
        }

        await using var transport = new StreamableHttpServerTransport(_loggerFactory)
        {
            Stateless = true,
        };

        var mcpServerOptions = _mcpServerOptionsFactory.Create(Options.DefaultName);
        mcpServerOptions.ScopeRequests = false;

        if (HttpServerTransportOptions.ConfigureSessionOptions is { } configureSessionOptions)
        {
            await configureSessionOptions(context.EnvironmentView, mcpServerOptions, context.RequestAborted);
        }

        await using var server = McpServer.Create(transport, mcpServerOptions, _loggerFactory, context.RequestServices);
        using var sessionClosedCts = new CancellationTokenSource();

#pragma warning disable MCPEXP002
        var runSessionAsync = HttpServerTransportOptions.RunSessionHandler ?? RunSessionAsync;
#pragma warning restore MCPEXP002
        var serverRunTask = runSessionAsync(context.EnvironmentView, server, sessionClosedCts.Token);

        try
        {
            InitializeSseResponse(context);
            var wroteResponse = await transport.HandlePostRequestAsync(message, context.ResponseBody, context.RequestAborted);
            if (!wroteResponse)
            {
                context.ClearResponseHeader("Content-Type");
                context.StatusCode = 202;
            }
        }
        finally
        {
            await transport.DisposeAsync();
            sessionClosedCts.Cancel();

            try
            {
                await serverRunTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Handles an incoming GET request from the OWIN environment.
    /// </summary>
    public Task HandleGetAsync(IDictionary<string, object> environment)
    {
        var context = new OwinMcpContext(environment);
        context.StatusCode = 405;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles an incoming DELETE request from the OWIN environment.
    /// </summary>
    public Task HandleDeleteAsync(IDictionary<string, object> environment)
    {
        var context = new OwinMcpContext(environment);
        context.StatusCode = 404;
        return Task.CompletedTask;
    }

    private static Task RunSessionAsync(IDictionary<string, object> _, McpServer session, CancellationToken cancellationToken)
        => session.RunAsync(cancellationToken);

    private static async Task<JsonRpcMessage?> ReadJsonRpcMessageAsync(OwinMcpContext context)
    {
        var message = await JsonSerializer.DeserializeAsync(context.RequestBody, MessageTypeInfo, context.RequestAborted);
        if (message is not null)
        {
            var protocolVersion = context.GetRequestHeader(McpProtocolVersionHeaderName);
            var isAuthenticated = context.User.Identity?.IsAuthenticated == true;
            if (isAuthenticated || !string.IsNullOrEmpty(protocolVersion))
            {
                message.Context ??= new();

                if (isAuthenticated)
                {
                    message.Context.User = context.User;
                }

                if (!string.IsNullOrEmpty(protocolVersion))
                {
                    message.Context.ProtocolVersion = protocolVersion;
                }
            }
        }

        return message;
    }

    private static void InitializeSseResponse(OwinMcpContext context)
    {
        context.StatusCode = 200;
        context.SetResponseHeader("Content-Type", "text/event-stream");
        context.SetResponseHeader("Cache-Control", "no-cache,no-store");
        context.SetResponseHeader("Content-Encoding", "identity");
        context.SetResponseHeader("X-Accel-Buffering", "no");
    }

    private static bool ValidateProtocolVersionHeader(OwinMcpContext context, [NotNullWhen(false)] out JsonRpcErrorDetail? errorDetail)
    {
        var protocolVersionHeader = context.GetRequestHeader(McpProtocolVersionHeaderName);
        if (!string.IsNullOrEmpty(protocolVersionHeader) && !SupportedProtocolVersions.Contains(protocolVersionHeader))
        {
            errorDetail = new JsonRpcErrorDetail
            {
                Code = (int)McpErrorCode.UnsupportedProtocolVersion,
                Message = $"Bad Request: The MCP-Protocol-Version header value '{protocolVersionHeader}' is not supported.",
                Data = JsonSerializer.SerializeToNode(
                    new UnsupportedProtocolVersionErrorData
                    {
                        Supported = SupportedProtocolVersions.ToArray(),
                        Requested = protocolVersionHeader,
                    },
                    UnsupportedProtocolVersionTypeInfo),
            };
            return false;
        }

        errorDetail = null;
        return true;
    }

    internal static bool ValidateMcpHeaders(
        OwinMcpContext context,
        JsonRpcMessage message,
        McpServerPrimitiveCollection<McpServerTool>? toolCollection,
        [NotNullWhen(false)] out string? errorMessage)
    {
        var protocolVersion = context.GetRequestHeader(McpProtocolVersionHeaderName);
        if (!McpHttpHeaders.SupportsStandardHeaders(protocolVersion))
        {
            errorMessage = null;
            return true;
        }

        if (message is not JsonRpcRequest and not JsonRpcNotification)
        {
            errorMessage = null;
            return true;
        }

        if (!context.ContainsRequestHeader(McpHttpHeaders.Method))
        {
            errorMessage = "Missing required Mcp-Method header.";
            return false;
        }

        var mcpMethodInHeader = context.GetRequestHeader(McpHttpHeaders.Method).Trim();
        var mcpMethodInBody = message switch
        {
            JsonRpcRequest request => request.Method,
            JsonRpcNotification notification => notification.Method,
            _ => null,
        };

        if (!string.Equals(mcpMethodInHeader, mcpMethodInBody, StringComparison.Ordinal))
        {
            errorMessage = $"Header mismatch: Mcp-Method header value '{mcpMethodInHeader}' does not match body value '{mcpMethodInBody}'.";
            return false;
        }

        if (mcpMethodInBody is not (RequestMethods.ToolsCall or RequestMethods.ResourcesRead or RequestMethods.PromptsGet))
        {
            errorMessage = null;
            return true;
        }

        if (!context.ContainsRequestHeader(McpHttpHeaders.Name))
        {
            errorMessage = "Missing required Mcp-Name header.";
            return false;
        }

        errorMessage = null;
        return true;
    }

    private static bool ClientAccepts(OwinMcpContext context, string mediaType)
    {
        var accept = context.GetRequestHeader("Accept");
        if (string.IsNullOrWhiteSpace(accept))
        {
            return false;
        }

        foreach (var value in accept.Split(','))
        {
            var candidate = value.Split(';')[0].Trim();
            if (candidate == "*/*" ||
                string.Equals(candidate, mediaType, StringComparison.OrdinalIgnoreCase) ||
                (candidate.EndsWith("/*", StringComparison.Ordinal) &&
                    mediaType.StartsWith(candidate.Substring(0, candidate.Length - 1), StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasJsonContentType(OwinMcpContext context)
    {
        var contentType = context.GetRequestHeader("Content-Type");
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        return string.Equals(contentType.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static Task WriteJsonRpcErrorAsync(OwinMcpContext context, string errorMessage, int statusCode, int errorCode = -32000)
    {
        var jsonRpcError = new JsonRpcError
        {
            Error = new()
            {
                Code = errorCode,
                Message = errorMessage,
            },
        };

        return WriteJsonRpcErrorAsync(context, jsonRpcError, statusCode);
    }

    private static Task WriteJsonRpcErrorDetailAsync(OwinMcpContext context, JsonRpcErrorDetail detail, int statusCode)
    {
        var jsonRpcError = new JsonRpcError { Error = detail };
        return WriteJsonRpcErrorAsync(context, jsonRpcError, statusCode);
    }

    private static async Task WriteJsonRpcErrorAsync(OwinMcpContext context, JsonRpcError jsonRpcError, int statusCode)
    {
        context.StatusCode = statusCode;
        context.SetResponseHeader("Content-Type", "application/json");
        await JsonSerializer.SerializeAsync(context.ResponseBody, jsonRpcError, ErrorTypeInfo, context.RequestAborted);
        await context.ResponseBody.FlushAsync(context.RequestAborted);
    }

    private static JsonTypeInfo<T> GetRequiredJsonTypeInfo<T>()
        => (JsonTypeInfo<T>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(T));
}
