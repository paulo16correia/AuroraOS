namespace Aurora.Core.Contracts;

public static class ScheduleTrigger
{
    public const string Once = "ONCE";
    public const string Cron = "CRON";
    public const string Interval = "INTERVAL";
    public const string EventCondition = "EVENT_CONDITION";

    public static bool IsKnown(string trigger) =>
        trigger is Once or Cron or Interval or EventCondition;
}

public static class ScheduleTarget
{
    public const string CycleTemplate = "CYCLE_TEMPLATE";
    public const string Goal = "GOAL";
    public const string Task = "TASK";

    public static bool IsKnown(string target) => target is CycleTemplate or Goal or Task;
}

public static class ScheduleStatus
{
    public const string Active = "ACTIVE";
    public const string Paused = "PAUSED";

    /// <summary>No further occurrences. Also where a deleted schedule ends up — see docs/adr/0032.</summary>
    public const string Expired = "EXPIRED";

    public const string Failed = "FAILED";
}

/// <summary>
/// What to do about occurrences that came due while Aurora was not running (RFC 026).
/// </summary>
/// <remarks>
/// The default is <see cref="Skip"/> because the alternative is an avalanche: a machine that was
/// off for a week must not wake up and fire a week of hourly jobs at once.
/// </remarks>
public static class MissedRunPolicy
{
    public const string Skip = "SKIP";

    /// <summary>Run the most recent missed occurrence once; record the rest as missed.</summary>
    public const string RunOnce = "RUN_ONCE";

    /// <summary>Record them and ask the person what to do. Aurora runs none of them on its own.</summary>
    public const string Ask = "ASK";

    public static bool IsKnown(string policy) => policy is Skip or RunOnce or Ask;
}

public static class ScheduleRunStatus
{
    public const string Due = "DUE";
    public const string Started = "STARTED";
    public const string Skipped = "SKIPPED";
    public const string Succeeded = "SUCCEEDED";
    public const string Failed = "FAILED";

    /// <summary>Came due and was not run. Recorded rather than dropped, so a gap is visible.</summary>
    public const string Missed = "MISSED";

    public static bool IsTerminal(string status) =>
        status is Skipped or Succeeded or Failed or Missed;
}

public static class QuietHoursPolicy
{
    /// <summary>Hold the occurrence until quiet hours end.</summary>
    public const string Defer = "DEFER";

    /// <summary>Run, but do not notify until quiet hours end.</summary>
    public const string RunSilently = "RUN_SILENTLY";

    /// <summary>Quiet hours do not apply. For recovery and safety work.</summary>
    public const string Ignore = "IGNORE";

    public static bool IsKnown(string policy) => policy is Defer or RunSilently or Ignore;
}

public sealed record Schedule(
    string Id,
    string OwnerId,
    string Title,
    string Trigger,
    string Timezone,
    string Expression,
    string? NextRunAtUtc,
    string? LastRunAtUtc,
    string Target,
    string? PayloadRef,
    bool Enabled,
    string QuietHoursPolicy,
    string MissedRunPolicy,
    string Status,
    /// <summary>Why the schedule stopped being active. Null while it is running normally.</summary>
    string? DisabledReason = null);

public sealed record ScheduleRun(
    string Id,
    string ScheduleId,
    string DueAtUtc,
    string? StartedAtUtc,
    string? FinishedAtUtc,
    string Status,
    string? CycleId,
    string? ResultRef,
    /// <summary>
    /// Identifies the occurrence, not the attempt. Derived from the schedule and the local wall
    /// time, which is what makes the repeated hour at the end of DST run once rather than twice.
    /// </summary>
    string IdempotencyKey);

/// <summary>What a caller asks for when creating a schedule.</summary>
public sealed record ScheduleRequest(
    string Title,
    string OwnerId,
    string Trigger,
    string Timezone,
    string Expression,
    string Target,
    string? PayloadRef = null,
    string QuietHoursPolicy = Contracts.QuietHoursPolicy.Defer,
    string MissedRunPolicy = Contracts.MissedRunPolicy.Skip,
    /// <summary>
    /// Whether running this reaches outside Aurora — sends a message, changes data, uses a
    /// connector. Declared by the caller and checked at creation (RFC 026 rule 3).
    /// </summary>
    bool ReachesOutsideAurora = false);

public sealed class SchedulingException : Exception
{
    public SchedulingException(string message) : base(message)
    {
    }
}
