namespace Aurora.Core.Contracts;

/// <summary>Lifecycle of a consent session.</summary>
public static class ConsentSessionStatus
{
    public const string Active = "ACTIVE";
    public const string Revoked = "REVOKED";
}

/// <summary>
/// A time-boxed grant that lets a principal repeat <b>read-only</b> approval-gated actions
/// without a fresh prompt (docs/adr/0010).
/// </summary>
/// <remarks>
/// Never covers a capability that declares effects. Design 0001 flagged the danger directly: a
/// reused session running subsequent writes is permanent autonomy with effects. The owner's
/// decision is that reuse is for reads only, so a write always costs a fresh, input-bound approval.
/// <para>
/// <see cref="ServerBootId"/> and <see cref="PolicyVersion"/> are part of the identity of the
/// session, not decoration: a restart or a policy change makes every existing session stop
/// matching, which is how "invalidate on restart" and "invalidate on policy change" are enforced
/// without a sweep job.
/// </para>
/// </remarks>
public sealed record ConsentSession(
    string SessionId,
    string PrincipalClientId,
    string PrincipalWindowsUser,
    string ServerBootId,
    string PolicyVersion,
    string Status,
    int ActionsUsed,
    int MaxActions,
    string CreatedAtUtc,
    string ExpiresAtUtc);

/// <summary>Whether a live session covered this request.</summary>
public enum ConsentSessionUseOutcome
{
    /// <summary>No live session for this principal; the caller must fall back to an approval.</summary>
    None,

    /// <summary>A live session covered the request and one unit of its budget was consumed.</summary>
    Used,
}

public sealed record ConsentSessionUse(ConsentSessionUseOutcome Outcome, string? SessionId = null);
