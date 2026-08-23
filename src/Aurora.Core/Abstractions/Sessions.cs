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
    /// Atomically finds a live session for this principal and spends one unit of its action
    /// budget. Returns <see cref="ConsentSessionUseOutcome.None"/> when there is no live session,
    /// when it has expired, when the budget is exhausted, or when it belongs to an earlier boot or
    /// policy version.
    /// </summary>
    Task<ConsentSessionUse> TryUseAsync(Principal principal, CancellationToken ct);

    /// <summary>Kill switch: revokes every active session and returns how many were revoked.</summary>
    Task<int> RevokeAllAsync(CancellationToken ct);

    /// <summary>Live sessions for the current boot and policy version, for the metrics gauge.</summary>
    Task<int> CountActiveAsync(CancellationToken ct);
}
