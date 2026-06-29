using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ModelContextProtocol.AspNet;

/// <summary>
/// Handles Streamable HTTP requests for an OWIN-based MCP server.
/// </summary>
public sealed class StreamableHttpHandler
{
    private const string McpSessionIdHeaderName = McpHttpHeaders.SessionId;
    private const string McpProtocolVersionHeaderName = McpHttpHeaders.ProtocolVersion;

    private static readonly HashSet<string> s_supportedProtocolVersions = new(StringComparer.Ordinal)
    {
        "2024-11-05",
        "2025-03-26",
        "2025-06-18",
        McpHttpHeaders.November2025ProtocolVersion,
        McpHttpHeaders.July2026ProtocolVersion,
    };

    private static readonly JsonTypeInfo<JsonRpcMessage> s_messageTypeInfo = GetRequiredJsonTypeInfo<JsonRpcMessage>();
    private static readonly JsonTypeInfo<JsonRpcError> s_errorTypeInfo = GetRequiredJsonTypeInfo<JsonRpcError>();
    private static readonly JsonTypeInfo<UnsupportedProtocolVersionErrorData> s_unsupportedProtocolVersionTypeInfo =
        GetRequiredJsonTypeInfo<UnsupportedProtocolVersionErrorData>();

    private readonly IOptions<McpServerOptions> _mcpServerOptionsSnapshot;
    private readonly IOptionsFactory<McpServerOptions> _mcpServerOptionsFactory;
    private readonly IOptions<HttpServerTransportOptions> _httpServerTransportOptions;
    private readonly StatefulSessionManager _sessionManager;
    private readonly IServiceProvider _applicationServices;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamableHttpHandler"/> class.
    /// </summary>
    public StreamableHttpHandler(
        IOptions<McpServerOptions> mcpServerOptionsSnapshot,
        IOptionsFactory<McpServerOptions> mcpServerOptionsFactory,
        IOptions<HttpServerTransportOptions> httpServerTransportOptions,
        StatefulSessionManager sessionManager,
        IServiceProvider applicationServices,
        ILoggerFactory loggerFactory)
    {
        _mcpServerOptionsSnapshot = mcpServerOptionsSnapshot;
        _mcpServerOptionsFactory = mcpServerOptionsFactory;
        _httpServerTransportOptions = httpServerTransportOptions;
        _sessionManager = sessionManager;
        _applicationServices = applicationServices;
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

        if (HttpServerTransportOptions.Stateless)
        {
            var sessionId = context.GetRequestHeader(McpSessionIdHeaderName);
            if (!string.IsNullOrEmpty(sessionId))
            {
                await WriteJsonRpcErrorAsync(context, "Bad Request: The Mcp-Session-Id header is not supported in stateless mode", 400);
                return;
            }
        }

        var toolCollection = _mcpServerOptionsSnapshot.Value?.ToolCollection;
        if (!ValidateMcpHeaders(context, message, toolCollection, out var errorMessage))
        {
            await WriteJsonRpcErrorAsync(context, errorMessage, 400, (int)McpErrorCode.HeaderMismatch);
            return;
        }

        var session = await GetOrCreateSessionAsync(context, message);
        if (session is null)
        {
            return;
        }

        await using var reference = await session.AcquireReferenceAsync(context.RequestAborted);

        InitializeSseResponse(context);
        var wroteResponse = await session.Transport.HandlePostRequestAsync(message, context.ResponseBody, context.RequestAborted);
        if (!wroteResponse)
        {
            context.ClearResponseHeader("Content-Type");
            context.StatusCode = 202;
        }
    }

    /// <summary>
    /// Handles an incoming GET request from the OWIN environment.
    /// </summary>
    public async Task HandleGetAsync(IDictionary<string, object> environment)
    {
        var context = new OwinMcpContext(environment);

        if (HttpServerTransportOptions.Stateless)
        {
            await WriteJsonRpcErrorAsync(context, "Bad Request: GET requests are not supported in stateless mode.", 400);
            return;
        }

        if (!ClientAccepts(context, "text/event-stream"))
        {
            await WriteJsonRpcErrorAsync(context, "Not Acceptable: Client must accept text/event-stream", 406);
            return;
        }

        var sessionId = context.GetRequestHeader(McpSessionIdHeaderName);
        if (string.IsNullOrEmpty(sessionId) || !_sessionManager.TryGetValue(sessionId, out var session))
        {
            await WriteJsonRpcErrorAsync(context, "Session not found", 404, -32001);
            return;
        }

        await using var reference = await session.AcquireReferenceAsync(context.RequestAborted);
        InitializeSseResponse(context);
        await session.Transport.HandleGetRequestAsync(context.ResponseBody, context.RequestAborted);
    }

    /// <summary>
    /// Handles an incoming DELETE request from the OWIN environment.
    /// </summary>
    public async Task HandleDeleteAsync(IDictionary<string, object> environment)
    {
        var context = new OwinMcpContext(environment);

        if (HttpServerTransportOptions.Stateless)
        {
            context.StatusCode = 404;
            return;
        }

        var sessionId = context.GetRequestHeader(McpSessionIdHeaderName);
        if (!string.IsNullOrEmpty(sessionId) && _sessionManager.TryRemove(sessionId, out var session))
        {
            await session.DisposeAsync();
        }
    }

    private async ValueTask<StreamableHttpSession?> GetOrCreateSessionAsync(OwinMcpContext context, JsonRpcMessage message)
    {
        var sessionId = context.GetRequestHeader(McpSessionIdHeaderName);

        if (IsJuly2026OrLaterProtocol(context))
        {
            if (!string.IsNullOrEmpty(sessionId))
            {
                await WriteJsonRpcErrorAsync(
                    context,
                    "Bad Request: Mcp-Session-Id is not supported by the 2026-07-28 and later protocol revisions (SEP-2567).",
                    400);
                return null;
            }

            if (!HttpServerTransportOptions.Stateless)
            {
                await WriteJsonRpcErrorAsync(
                    context,
                    $"Bad Request: Starting with protocol version '{McpHttpHeaders.July2026ProtocolVersion}', Streamable HTTP does not support sessions.",
                    400,
                    (int)McpErrorCode.UnsupportedProtocolVersion);
                return null;
            }

            return await StartNewSessionAsync(context);
        }

        if (string.IsNullOrEmpty(sessionId))
        {
            if (!HttpServerTransportOptions.Stateless && message is not JsonRpcRequest { Method: RequestMethods.Initialize })
            {
                await WriteJsonRpcErrorAsync(
                    context,
                    "Bad Request: A new session can only be created by an initialize request. Include a valid Mcp-Session-Id header for non-initialize requests.",
                    400);
                return null;
            }

            return await StartNewSessionAsync(context);
        }

        if (HttpServerTransportOptions.Stateless)
        {
            await WriteJsonRpcErrorAsync(context, "Bad Request: The Mcp-Session-Id header is not supported in stateless mode", 400);
            return null;
        }

        if (!_sessionManager.TryGetValue(sessionId, out var session))
        {
            await WriteJsonRpcErrorAsync(context, "Session not found", 404, -32001);
            return null;
        }

        context.SetResponseHeader(McpSessionIdHeaderName, session.Id);
        return session;
    }

    private async ValueTask<StreamableHttpSession> StartNewSessionAsync(OwinMcpContext context)
    {
        string sessionId;
        StreamableHttpServerTransport transport;

        if (HttpServerTransportOptions.Stateless)
        {
            sessionId = string.Empty;
            transport = new StreamableHttpServerTransport(_loggerFactory)
            {
                Stateless = true,
            };
        }
        else
        {
            sessionId = Guid.NewGuid().ToString("N");
            transport = new StreamableHttpServerTransport(_loggerFactory)
            {
                SessionId = sessionId,
            };
            context.SetResponseHeader(McpSessionIdHeaderName, sessionId);
        }

        return await CreateSessionAsync(context, transport, sessionId);
    }

    private async ValueTask<StreamableHttpSession> CreateSessionAsync(
        OwinMcpContext context,
        StreamableHttpServerTransport transport,
        string sessionId)
    {
        var mcpServerServices = _applicationServices;
        var mcpServerOptions = _mcpServerOptionsSnapshot.Value;

        if (HttpServerTransportOptions.Stateless || HttpServerTransportOptions.ConfigureSessionOptions is not null)
        {
            mcpServerOptions = _mcpServerOptionsFactory.Create(Options.DefaultName);

            if (HttpServerTransportOptions.Stateless)
            {
                mcpServerServices = context.RequestServices;
                mcpServerOptions.ScopeRequests = false;
            }

            if (HttpServerTransportOptions.ConfigureSessionOptions is { } configureSessionOptions)
            {
                await configureSessionOptions(context.EnvironmentView, mcpServerOptions, context.RequestAborted);
            }
        }

        var server = McpServer.Create(transport, mcpServerOptions, _loggerFactory, mcpServerServices);
        var session = new StreamableHttpSession(sessionId, transport, server, _sessionManager);

#pragma warning disable MCPEXP002
        var runSessionAsync = HttpServerTransportOptions.RunSessionHandler ?? RunSessionAsync;
#pragma warning restore MCPEXP002
        session.ServerRunTask = runSessionAsync(context.EnvironmentView, server, session.SessionClosed);

        return session;
    }

    private static Task RunSessionAsync(IDictionary<string, object> _, McpServer session, CancellationToken cancellationToken)
        => session.RunAsync(cancellationToken);

    private static async Task<JsonRpcMessage?> ReadJsonRpcMessageAsync(OwinMcpContext context)
    {
        var message = await JsonSerializer.DeserializeAsync(context.RequestBody, s_messageTypeInfo, context.RequestAborted);
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
        if (!string.IsNullOrEmpty(protocolVersionHeader) && !s_supportedProtocolVersions.Contains(protocolVersionHeader))
        {
            errorDetail = new JsonRpcErrorDetail
            {
                Code = (int)McpErrorCode.UnsupportedProtocolVersion,
                Message = $"Bad Request: The MCP-Protocol-Version header value '{protocolVersionHeader}' is not supported.",
                Data = JsonSerializer.SerializeToNode(
                    new UnsupportedProtocolVersionErrorData
                    {
                        Supported = s_supportedProtocolVersions.ToArray(),
                        Requested = protocolVersionHeader,
                    },
                    s_unsupportedProtocolVersionTypeInfo),
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

    private static bool IsJuly2026OrLaterProtocol(OwinMcpContext context)
    {
        var protocolVersionHeader = context.GetRequestHeader(McpProtocolVersionHeaderName);
        return McpHttpHeaders.IsJuly2026OrLaterProtocolVersion(protocolVersionHeader);
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
        await JsonSerializer.SerializeAsync(context.ResponseBody, jsonRpcError, s_errorTypeInfo, context.RequestAborted);
        await context.ResponseBody.FlushAsync(context.RequestAborted);
    }

    private static JsonTypeInfo<T> GetRequiredJsonTypeInfo<T>()
        => (JsonTypeInfo<T>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(T));
}
