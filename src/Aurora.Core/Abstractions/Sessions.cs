using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>Identity of this server process, used to bind grants to a single run.</summary>
public interface IServerIdentity
{
    /// <summary>Regenerated on every start, so grants never survive a restart.</summary>
    string BootId { get; }
}

/// <summary>
/// Store of time-boxed consent sessions (docs/adr/0010). Sessions cover read-only actions only;
/// enforcing that is the gate's job, not the store's.
/// </summary>
public interface IConsentSessionStore
{
    /// <summary>
    /// Opens a session for this principal, bound to the current boot and policy version. An
    /// existing live session is returned rather than duplicated.
    /// </summary>
    Task<ConsentSession> OpenAsync(Principal principal, CancellationToken ct);

    /// <summary>
    /// Opens a session that also covers the named effectful actions, for a bounded time and count.
    /// </summary>
    /// <remarks>
    /// The named actions are the only ones it adds. Everything else it covers is what any session
    /// covers — reads — and everything it does not name still costs an explicit decision.
    /// </remarks>
    Task<ConsentSession> OpenAsync(
        Principal principal, IReadOnlyList<string> coveredActions, TimeSpan lifetime,
        int maxActions, CancellationToken ct);

    /// <summary>
    /// Atomically finds a live session for this principal and spends one unit of its action
    /// budget. Returns <see cref="ConsentSessionUseOutcome.None"/> when there is no live session,
    /// when it has expired, when the budget is exhausted, or when it belongs to an earlier boot or
    /// policy version.
    /// </summary>
    Task<ConsentSessionUse> TryUseAsync(Principal principal, CancellationToken ct);

    /// <summary>
    /// Spends one unit of a live session that covers <paramref name="actionId"/>.
    /// </summary>
    /// <remarks>
    /// Separate from the unscoped overload because the question is different: that one asks
    /// whether there is a session at all, and this asks whether the session in front of it agreed
    /// to this particular thing.
    /// </remarks>
    Task<ConsentSessionUse> TryUseAsync(
        Principal principal, string actionId, CancellationToken ct);

    /// <summary>Kill switch: revokes every active session and returns how many were revoked.</summary>
    Task<int> RevokeAllAsync(CancellationToken ct);

    /// <summary>Live sessions for the current boot and policy version, for the metrics gauge.</summary>
    Task<int> CountActiveAsync(CancellationToken ct);
}
