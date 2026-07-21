namespace Aurora.Server.Security;

/// <summary>
/// Anti DNS-rebinding guard. Only requests whose Host header is loopback are served, and any
/// Origin header (if present) must also be loopback. Runs before authentication so a hostile Host
/// never reaches the token check.
/// </summary>
public sealed class LoopbackGuardMiddleware
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "127.0.0.1",
        "::1",
        "[::1]",
    };

    private readonly RequestDelegate _next;

    public LoopbackGuardMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var host = context.Request.Host.Host;
        if (!AllowedHosts.Contains(host))
        {
            await RejectAsync(context, StatusCodes.Status421MisdirectedRequest, "host_not_allowed");
            return;
        }

        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin))
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) || !AllowedHosts.Contains(originUri.Host))
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
