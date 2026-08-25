using System.Security.Cryptography;
using System.Text;

namespace Aurora.Server.Security;

/// <summary>
/// Requires either the agent's bearer token or a person's operator session on every request.
/// </summary>
/// <remarks>
/// Two credentials, deliberately, and the request is stamped with which one it carried. The agent
/// holds the bearer token; only a person holds a session minted on the server's console. Endpoints
/// that decide something ask for the second one specifically (RFC 11), so the agent cannot approve
/// its own request by calling the panel's API instead of the tool.
/// </remarks>
public sealed class BearerAuthMiddleware
{
    private const string Prefix = "Bearer ";
    private const int MaxAuthorizationHeaderLength = 8 * 1024;

    private readonly RequestDelegate _next;
    private readonly byte[] _expectedDigest;

    public BearerAuthMiddleware(RequestDelegate next, AuroraServerOptions options)
    {
        _next = next;
        _expectedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(options.BearerToken));
    }

    /// <summary>
    /// The one path that runs without a credential: exchanging a printed link for a session.
    /// </summary>
    /// <remarks>
    /// Named here rather than mapped earlier, because every endpoint executes after the whole
    /// middleware pipeline regardless of where it was registered. The exemption is narrow and the
    /// endpoint is not open: it refuses anything that is not an unexpired, unredeemed grant, and
    /// the loopback guard still applies, so the link is only usable from this machine.
    /// </remarks>
    private const string SessionExchangePath = "/ui/session";

    public async Task InvokeAsync(HttpContext context, OperatorSessions sessions)
    {
        if (context.Request.Path.Equals(SessionExchangePath, StringComparison.Ordinal))
        {
            await _next(context);
            return;
        }

        // The person's session is checked first, so a browser that also happens to carry a bearer
        // token is still recorded as the operator rather than as the agent.
        if (sessions.IsActive(context.Request.Cookies[OperatorSessions.CookieName]))
        {
            context.Items[RequestActor.ItemKey] = RequestActor.Operator;
            await _next(context);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();

        // Compare fixed-size SHA-256 digests so neither the comparison nor the fast-path on a length
        // mismatch leaks the configured token's length. Cap the header first to bound the work.
        if (header.Length > MaxAuthorizationHeaderLength
            || !header.StartsWith(Prefix, StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(header[Prefix.Length..])), _expectedDigest))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                error = "invalid_token",
                error_description = "unauthorized",
            });
            return;
        }

        context.Items[RequestActor.ItemKey] = RequestActor.Agent;
        await _next(context);
    }
}
