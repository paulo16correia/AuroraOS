namespace Aurora.Server.Security;

/// <summary>
/// Anti DNS-rebinding guard. A request is served only if its Host — and its Origin, when it has
/// one — is loopback or a name this deployment declared. Runs before authentication so a hostile
/// Host never reaches the token check.
/// </summary>
/// <remarks>
/// The declared names exist for one deployment shape: behind a reverse proxy, where the forwarded
/// Host is the public name and loopback would reject every real request. It is an allowlist the
/// operator writes, never a switch that turns the guard off — and binding beyond loopback without
/// writing one is refused at startup rather than silently accepted.
/// </remarks>
public sealed class LoopbackGuardMiddleware
{
    private static readonly string[] Loopback = ["localhost", "127.0.0.1", "::1", "[::1]"];

    private readonly RequestDelegate _next;
    private readonly HashSet<string> _allowedHosts;

    public LoopbackGuardMiddleware(RequestDelegate next, AuroraServerOptions options)
    {
        _next = next;
        _allowedHosts = new HashSet<string>(
            Loopback.Concat(options.AllowedHosts), StringComparer.OrdinalIgnoreCase);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var host = context.Request.Host.Host;
        if (!_allowedHosts.Contains(host))
        {
            await RejectAsync(context, StatusCodes.Status421MisdirectedRequest, "host_not_allowed");
            return;
        }

        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin))
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) || !_allowedHosts.Contains(originUri.Host))
            {
                await RejectAsync(context, StatusCodes.Status403Forbidden, "origin_not_allowed");
                return;
            }
        }

        await _next(context);
    }

    private static async Task RejectAsync(HttpContext context, int statusCode, string reason)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync(reason);
    }
}
