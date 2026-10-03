using System.Reflection;
using Aurora.Core.Abstractions;
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

        // Where a person signs in with the operator passphrase, for an Aurora that runs as a service
        // and so has no console to print a link on.
        app.MapGet("/ui/login", (Func<HttpContext, Task<IResult>>)(context => ServeAsync("login.html", context)));

        app.MapGet("/ui/{**path}", (string? path, HttpContext context) =>
            ServeAsync(string.IsNullOrWhiteSpace(path) || path == "/" ? "index.html" : path, context));

        return app;
    }

    private static async Task<IResult> ServeAsync(string name, HttpContext context)
    {
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

        SetCookie(context, session);
        return Results.Redirect("/ui/");
    }

    /// <summary>
    /// Opens a session for a person who knows the operator passphrase.
    /// </summary>
    /// <remarks>
    /// The way in for an Aurora that runs under its own account, as a service, with no console of
    /// its own to print a link on. It is the same secret <c>aurora_approve</c> asks for, checked by
    /// the same authenticator, so the same lockout covers both: five failures, wherever they were
    /// made, and the correct passphrase is refused too until the lockout ends. A failure also counts
    /// towards the security watch, like a wrong bearer token.
    /// </remarks>
    public static async Task<IResult> SignInAsync(
        PassphraseSignIn body, HttpContext context, OperatorSessions sessions,
        IPassphraseAuthenticator passphrase, ISecurityWatch watch)
    {
        if (!passphrase.IsEnrolled)
        {
            return Results.Json(
                new
                {
                    error = "passphrase_not_enrolled",
                    error_description = "No operator passphrase is enrolled. Enrol one with "
                        + "'enroll-passphrase', or open the panel with the link 'ui' prints.",
                },
                statusCode: StatusCodes.Status404NotFound);
        }

        PassphraseCheck check = passphrase.Verify(body.Passphrase);

        switch (check.Outcome)
        {
            case PassphraseOutcome.Verified:
                SetCookie(context, sessions.Open());
                return Results.Json(new { signed_in = true });

            case PassphraseOutcome.LockedOut:
                return Results.Json(
                    new
                    {
                        error = "locked_out",
                        error_description = "Too many failed attempts. Try again after "
                            + (check.LockedUntilUtc?.ToString("u") ?? "the lockout ends") + ".",
                    },
                    statusCode: StatusCodes.Status429TooManyRequests);

            default:
                await watch.AuthenticationFailedAsync("panel", context.RequestAborted);
                return Results.Json(
                    new { error = "invalid_passphrase", error_description = "That passphrase is not valid." },
                    statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    private static void SetCookie(HttpContext context, string session) =>
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
}

/// <summary>What the sign-in page sends.</summary>
public sealed record PassphraseSignIn(string? Passphrase);
