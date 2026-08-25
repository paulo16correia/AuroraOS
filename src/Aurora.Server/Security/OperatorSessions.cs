using System.Collections.Concurrent;
using System.Security.Cryptography;
using Aurora.Core.Abstractions;

namespace Aurora.Server.Security;

/// <summary>
/// The person's credential, which the agent does not have (RFC 11).
/// </summary>
/// <remarks>
/// The bearer token belongs to the MCP client. If the control panel accepted only that, then
/// everything the panel can do — approve, correct, forget, revoke — would be something the agent
/// could do to itself by calling the same endpoint. An operator session is a separate credential,
/// minted on the server's own console and handed to a browser, and it is what the deciding
/// endpoints actually require.
/// <para>
/// Held in memory on purpose. A session that outlived the process would be a standing grant nobody
/// remembers issuing; a restart ends every one of them, which is the behaviour an operator would
/// assume anyway.
/// </para>
/// </remarks>
public sealed class OperatorSessions
{
    /// <summary>How long a printed link stays usable. Long enough to paste, short enough to forget.</summary>
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromMinutes(10);

    /// <summary>How long a redeemed session lasts without being used again.</summary>
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    public const string CookieName = "aurora_operator";

    private readonly ConcurrentDictionary<string, DateTimeOffset> _grants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);
    private readonly IClock _clock;

    public OperatorSessions(IClock clock) => _clock = clock;

    /// <summary>Issues a one-time grant to be carried in the link the console prints.</summary>
    public string Mint()
    {
        var grant = NewToken();
        _grants[grant] = _clock.UtcNow + GrantLifetime;
        return grant;
    }

    /// <summary>
    /// Exchanges a grant for a session. One-time: the grant is consumed whether or not it was valid.
    /// </summary>
    /// <remarks>
    /// A link that keeps working is a link that keeps working for whoever finds it in a shell
    /// history or a screenshot.
    /// </remarks>
    public string? Redeem(string? grant)
    {
        if (string.IsNullOrWhiteSpace(grant) || !_grants.TryRemove(grant, out DateTimeOffset expires))
        {
            return null;
        }

        if (expires <= _clock.UtcNow)
        {
            return null;
        }

        var session = NewToken();
        _sessions[session] = _clock.UtcNow + SessionLifetime;
        return session;
    }

    public bool IsActive(string? session)
    {
        if (string.IsNullOrWhiteSpace(session) || !_sessions.TryGetValue(session, out DateTimeOffset expires))
        {
            return false;
        }

        if (expires > _clock.UtcNow)
        {
            return true;
        }

        _sessions.TryRemove(session, out _);
        return false;
    }

    /// <summary>Ends one session. The panel's sign-out, and it takes effect immediately.</summary>
    public void End(string? session)
    {
        if (!string.IsNullOrWhiteSpace(session))
        {
            _sessions.TryRemove(session, out _);
        }
    }

    public int Active => _sessions.Count(s => s.Value > _clock.UtcNow);

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

/// <summary>Which surface a request arrived on. Stamped by the auth middleware.</summary>
public static class RequestActor
{
    public const string ItemKey = "aurora.actor";

    /// <summary>A person, holding a session minted on the server's console.</summary>
    public const string Operator = "operator";

    /// <summary>The MCP client, holding the bearer token.</summary>
    public const string Agent = "agent";

    public static bool IsOperator(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var actor)
        && string.Equals(actor as string, Operator, StringComparison.Ordinal);
}
