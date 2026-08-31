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
    string PrincipalOsUser,
    string ServerBootId,
    string PolicyVersion,
    string Status,
    int ActionsUsed,
    int MaxActions,
    string CreatedAtUtc,
    string ExpiresAtUtc,
    /// <summary>
    /// The effectful actions this session covers, beyond the read-only ones it always covers.
    /// </summary>
    /// <remarks>
    /// Empty for an ordinary session, which is the rule Aurora started with: approving a read
    /// opens a session for the next reads, and approving a write opens nothing. That rule is what
    /// separates a conversation from a licence to act, and it is not relaxed here — it is made
    /// nameable. A capability may declare that approving it covers specific other actions, and
    /// only those, for the minutes and the count the owner agreed to (docs/adr/0070).
    /// <para>
    /// A voice conversation is the case it was written for: approving every sentence is not a
    /// conversation, and nobody will sit at a keyboard authorising each one while their friends
    /// talk. What makes it safe to grant is not that speech is harmless but that the window is
    /// small, named, and ended by three capabilities that ask nobody.
    /// </para>
    /// </remarks>
    IReadOnlyList<string>? CoveredActions = null);

/// <summary>Whether a live session covered this request.</summary>
public enum ConsentSessionUseOutcome
{
    /// <summary>No live session for this principal; the caller must fall back to an approval.</summary>
    None,

    /// <summary>A live session covered the request and one unit of its budget was consumed.</summary>
    Used,
}

public sealed record ConsentSessionUse(ConsentSessionUseOutcome Outcome, string? SessionId = null);
