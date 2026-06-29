using System.Security.Claims;

namespace ModelContextProtocol.AspNet;

internal sealed class OwinMcpContext(IDictionary<string, object> environment)
{
    public IDictionary<string, object> EnvironmentView => environment;

    public string Method => GetRequired<string>("owin.RequestMethod");
    public string Path => GetRequired<string>("owin.RequestPath");
    public Stream RequestBody => GetRequired<Stream>("owin.RequestBody");
    public Stream ResponseBody => GetRequired<Stream>("owin.ResponseBody");
    public IDictionary<string, string[]> RequestHeaders => GetRequired<IDictionary<string, string[]>>("owin.RequestHeaders");
    public IDictionary<string, string[]> ResponseHeaders => GetRequired<IDictionary<string, string[]>>("owin.ResponseHeaders");

    public CancellationToken RequestAborted =>
        environment.TryGetValue("owin.CallCancelled", out var value) && value is CancellationToken cancellationToken
            ? cancellationToken
            : CancellationToken.None;

    public IServiceProvider RequestServices =>
        environment.TryGetValue("mcp.RequestServices", out var value) && value is IServiceProvider services
            ? services
            : throw new InvalidOperationException("No MCP request services were available for this OWIN request.");

    public ClaimsPrincipal User
    {
        get
        {
            if (environment.TryGetValue("server.User", out var serverUser) && serverUser is ClaimsPrincipal serverPrincipal)
            {
                return serverPrincipal;
            }

            if (environment.TryGetValue("owin.RequestUser", out var owinUser) && owinUser is ClaimsPrincipal owinPrincipal)
            {
                return owinPrincipal;
            }

            return new ClaimsPrincipal(new ClaimsIdentity());
        }
    }

    public int StatusCode
    {
        get => environment.TryGetValue("owin.ResponseStatusCode", out var value) && value is int statusCode ? statusCode : 200;
        set => environment["owin.ResponseStatusCode"] = value;
    }

    public string GetRequestHeader(string name)
    {
        return RequestHeaders.TryGetValue(name, out var values) && values.Length > 0
            ? string.Join(",", values)
            : string.Empty;
    }

    public bool ContainsRequestHeader(string name) => RequestHeaders.ContainsKey(name);

    public void SetResponseHeader(string name, string value)
    {
        ResponseHeaders[name] = new[] { value };
    }

    public void ClearResponseHeader(string name)
    {
        ResponseHeaders.Remove(name);
    }

    private T GetRequired<T>(string key)
    {
        if (environment.TryGetValue(key, out var value) && value is T typed)
        {
            return typed;
        }

        throw new InvalidOperationException($"The OWIN environment did not contain a '{key}' value.");
    }
}
