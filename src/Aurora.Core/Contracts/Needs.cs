namespace Aurora.Core.Contracts;

public static class NeedKind
{
    public const string Safety = "SAFETY";
    public const string Obligation = "OBLIGATION";
    public const string Maintenance = "MAINTENANCE";
    public const string Consolidation = "CONSOLIDATION";
    public const string Communication = "COMMUNICATION";
    public const string Recovery = "RECOVERY";

    /// <summary>
    /// Kinds that outrank the person's own requests (RFC 031 rule 3).
    /// </summary>
    /// <remarks>
    /// Exactly two, and both are about the system being unable to keep its promises. Anything else
    /// waiting on Aurora waits behind what the person actually asked for.
    /// </remarks>
    public static bool IsIncident(string kind) => kind is Safety or Recovery;

    public static bool IsKnown(string kind) =>
        kind is Safety or Obligation or Maintenance or Consolidation or Communication or Recovery;
}

public static class NeedStatus
{
    public const string Detected = "DETECTED";
    public const string Acknowledged = "ACKNOWLEDGED";
    public const string Planned = "PLANNED";
    public const string Satisfied = "SATISFIED";
    public const string Deferred = "DEFERRED";
    public const string Expired = "EXPIRED";
}

public static class NeedOwner
{
    public const string System = "SYSTEM";
    public const string User = "USER";
}

/// <summary>
/// A standing operating condition that deserves attention (RFC 031).
/// </summary>
/// <remarks>
/// Not an emotion and not an authorisation. A need is a candidate for focus when Aurora is
/// available, and the most it can do on its own is draft a goal — which then goes through the cycle
/// like anything else.
/// </remarks>
public sealed record Need(
    string Id,
    string Kind,
    string SubjectRef,
    double Intensity,
    int Priority,
    IReadOnlyList<string> EvidenceRefs,
    /// <summary>
    /// How this need will be known to be met. Required: a need with no measurable end is a
    /// complaint, and it would never stop being urgent (rule 1).
    /// </summary>
    string SatisfactionCondition,
    string? EarliestActionAtUtc,
    string? ExpiresAtUtc,
    string? RecommendedGoalRef,
    string Status,
    IReadOnlyList<string> PolicyConstraints,
    string Owner,
    string DetectedAtUtc,
    string? SatisfiedEvidenceRef = null);

/// <summary>
/// The observable conditions needs are derived from.
/// </summary>
/// <remarks>
/// Every field is a count or an age somebody measured, not a mood. Detection is a function of this
/// record and the live signals, so what Aurora says it needs can always be traced back to something
/// that was actually observed.
/// </remarks>
/// <remarks>
/// Every field is nullable, and null means <i>not measured</i> rather than zero. The difference
/// matters: reporting "no approvals are pending" because nobody looked is a claim Aurora would be
/// making up, and it is exactly the claim that stops a need from ever being noticed.
/// </remarks>
public sealed record NeedsSnapshot(
    int? DeadLetters = null,
    int? PendingApprovals = null,
    int? MissedScheduleRuns = null,
    int? OverdueGoals = null,
    int? UnreconciledReservations = null,
    TimeSpan? SinceLastBackup = null,
    int? UnconsolidatedMemories = null)
{
    /// <summary>Which conditions this snapshot did not look at.</summary>
    public IReadOnlyList<string> Unmeasured =>
        new[]
        {
            (DeadLetters is null, "dead_letters"),
            (PendingApprovals is null, "pending_approvals"),
            (MissedScheduleRuns is null, "missed_schedule_runs"),
            (OverdueGoals is null, "overdue_goals"),
            (UnreconciledReservations is null, "unreconciled_reservations"),
            (SinceLastBackup is null, "since_last_backup"),
            (UnconsolidatedMemories is null, "unconsolidated_memories"),
        }
        .Where(pair => pair.Item1).Select(pair => pair.Item2).ToList();
}

public sealed class NeedException : Exception
{
    public NeedException(string message) : base(message)
    {
    }
}
