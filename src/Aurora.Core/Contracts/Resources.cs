namespace Aurora.Core.Contracts;

public static class ResourceStatus
{
    public const string Normal = "NORMAL";
    public const string Constrained = "CONSTRAINED";
    public const string Critical = "CRITICAL";

    /// <summary>
    /// The metric could not be read. Distinct from healthy, deliberately.
    /// </summary>
    /// <remarks>
    /// RFC 033: a metric that is unavailable is assumed UNKNOWN and admission becomes conservative.
    /// Treating "I could not measure it" as "it is fine" is how a system becomes least reliable
    /// exactly when there is most going on.
    /// </remarks>
    public const string Unknown = "UNKNOWN";
}

public static class NetworkState
{
    public const string Up = "UP";
    public const string Down = "DOWN";
    public const string Unknown = "UNKNOWN";
}

/// <summary>What admission decided about a piece of work (RFC 033).</summary>
public static class Admission
{
    public const string Allow = "ALLOW";

    /// <summary>Not now. The work is fine; the moment is not.</summary>
    public const string Defer = "DEFER";

    public const string Deny = "DENY";
}

/// <summary>
/// How urgent-by-nature a piece of work is, for admission purposes (RFC 033 rule 1).
/// </summary>
/// <remarks>
/// Not the same as risk. Risk is about what an action could damage; this is about what must still
/// happen when there is not enough to go round. Security, recovery and work a person confirmed
/// keep their reserve; curiosity, indexing and maintenance are what gets postponed first.
/// </remarks>
public static class WorkClass
{
    /// <summary>Security, recovery, and anything a person is waiting on.</summary>
    public const string Essential = "ESSENTIAL";

    public const string Ordinary = "ORDINARY";

    /// <summary>Curiosity, indexing, consolidation. The first thing to give way.</summary>
    public const string Discretionary = "DISCRETIONARY";

    public static bool IsKnown(string workClass) =>
        workClass is Essential or Ordinary or Discretionary;
}

public sealed record ResourceState(
    string Id,
    string ObservedAtUtc,
    double? CpuPct,
    double? MemoryPct,
    double? DiskPct,
    string NetworkState,
    int QueueDepth,
    int ActiveWorkers,
    double ModelCostToday,
    string RateLimitState,
    /// <summary>
    /// Operational capacity, 0..1. A product metaphor for how much room there is to work; it does
    /// not represent a biological or emotional state.
    /// </summary>
    double OperationalEnergy,
    string Status,
    /// <summary>Which metrics could not be read. Empty when everything was measurable.</summary>
    IReadOnlyList<string> Unmeasured,
    /// <summary>
    /// Room left on the disk, which is what the disk's status is actually decided on.
    /// </summary>
    /// <remarks>
    /// Reported beside <c>DiskPct</c> rather than instead of it, because on a large disk the two
    /// stop agreeing and only one of them answers "can Aurora write". Null where the platform
    /// reported no figure.
    /// </remarks>
    long? DiskFreeBytes = null);

public sealed record ResourceBudget(
    string Id,
    string Scope,
    double MaxCost,
    int MaxConcurrency,
    TimeSpan TimeWindow,
    /// <summary>Concurrency held back for essential work, so it never queues behind housekeeping.</summary>
    int ReserveForCritical,
    string? PolicyId = null)
{
    public static ResourceBudget Default { get; } =
        new("budget/default", "instance", MaxCost: 10.0, MaxConcurrency: 4,
            TimeWindow: TimeSpan.FromDays(1), ReserveForCritical: 1);
}

/// <summary>A claim on capacity, held for the duration of one piece of work.</summary>
public sealed record Reservation(
    string Id, string WorkRef, string WorkClass, double EstimatedCost, string TakenAtUtc);

public sealed record AdmissionResult(
    string Decision, string Reason, string? ReservationId = null);

public sealed class ResourceException : Exception
{
    public ResourceException(string message) : base(message)
    {
    }
}
