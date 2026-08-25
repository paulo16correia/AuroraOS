using System.Reflection;
using Aurora.Core.Contracts;
using Aurora.Server.Api;
using Aurora.Server.Security;

namespace Aurora.Server.Ui;

/// <summary>
/// Serves the control panel and turns a printed link into a session (RFC 11).
/// </summary>
/// <remarks>
/// The assets are embedded in the assembly rather than read from disk: a panel that could be
/// changed by editing a file next to the binary would be a way to put arbitrary script in front of
/// an operator who is about to approve something.
/// </remarks>
public static class UiEndpoints
{
    private static readonly Assembly Assembly = typeof(UiEndpoints).Assembly;

    private static readonly IReadOnlyDictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".html"] = "text/html; charset=utf-8",
            [".css"] = "text/css; charset=utf-8",
            [".js"] = "text/javascript; charset=utf-8",
            [".svg"] = "image/svg+xml",
        };

    public static WebApplication MapAuroraUi(this WebApplication app)
    {
        app.MapPost("/ui/session/end", (HttpContext context, OperatorSessions sessions) =>
        {
            sessions.End(context.Request.Cookies[OperatorSessions.CookieName]);
            context.Response.Cookies.Delete(OperatorSessions.CookieName);
            return Results.Ok(new { ended = true });
        });

        // Who the panel is talking to, so it can say so rather than assume.
        app.MapGet("/ui/whoami", (HttpContext context) =>
            Results.Json(new
            {
                actor = RequestActor.IsOperator(context) ? RequestActor.Operator : RequestActor.Agent,
                api_version = ApiVersion.Current,
            }));

        app.MapGet("/ui/{**path}", async (string? path, HttpContext context) =>
        {
            var name = string.IsNullOrWhiteSpace(path) || path == "/" ? "index.html" : path;
            if (name.Contains("..", StringComparison.Ordinal))
            {
                return Results.NotFound();
            }

            await using Stream? asset = Assembly.GetManifestResourceStream($"Aurora.Server.Ui.{name}");
            if (asset is null)
            {
                return Results.NotFound();
            }

            using var reader = new StreamReader(asset);
            var body = await reader.ReadToEndAsync(context.RequestAborted);

            // No inline script, no remote origin, nothing framed. The panel is where approvals are
            // decided, so it is the last page in the system that should be able to load anything.
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; "
                + "connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";

            return Results.Content(
                body, ContentTypes.GetValueOrDefault(Path.GetExtension(name), "text/plain; charset=utf-8"));
        });

        return app;
    }
}

/// <summary>
/// Turns the link the console printed into a session cookie.
/// </summary>
/// <remarks>
/// Mapped ahead of the auth middleware in <c>Program</c>, because the browser following the link
/// does not hold a credential yet — that is what it has come to collect. Still behind the loopback
/// guard, so the link is only usable from this machine.
/// </remarks>
public static class UiSessionExchange
{
    public static IResult Redeem(string? grant, HttpContext context, OperatorSessions sessions)
    {
        var session = sessions.Redeem(grant);
        if (session is null)
        {
            return Results.Content(
                "This link is not valid any more. Run 'ui' on the Aurora console for a new one.",
                "text/plain; charset=utf-8", statusCode: StatusCodes.Status401Unauthorized);
        }

        context.Response.Cookies.Append(
            OperatorSessions.CookieName, session,
            new CookieOptions
            {
                // Not readable from script, not sent cross-site, and not written to disk: the
                // session should end with the browser as well as with the server.
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = context.Request.IsHttps,
                Path = "/",
            });

        return Results.Redirect("/ui/");
    }
}
