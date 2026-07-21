using System.Security.Cryptography;
using System.Text;

namespace Aurora.Server.Security;

/// <summary>
/// Requires a local high-entropy bearer token on every request. Comparison is constant-time.
/// </summary>
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

    public async Task InvokeAsync(HttpContext context)
    {
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
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("unauthorized");
            return;
        }

        await _next(context);
    }
}
