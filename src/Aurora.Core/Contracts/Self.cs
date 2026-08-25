namespace Aurora.Core.Contracts;

public static class OperationalState
{
    public const string Booting = "BOOTING";
    public const string Ready = "READY";
    public const string Busy = "BUSY";
    public const string Waiting = "WAITING";

    /// <summary>Working, and less than it should. Observed, never inferred from silence.</summary>
    public const string Degraded = "DEGRADED";

    public const string Paused = "PAUSED";

    /// <summary>Reconciling what a restart left in the air. Not READY until that is finished.</summary>
    public const string Recovering = "RECOVERING";

    public static bool IsKnown(string state) =>
        state is Booting or Ready or Busy or Waiting or Degraded or Paused or Recovering;

    /// <summary>States in which Aurora will not start new work of its own.</summary>
    public static bool Holds(string state) => state is Paused or Recovering or Booting;
}

/// <summary>What is installed, what is switched off, and what each thing may reach.</summary>
public sealed record CapabilitySnapshot(
    IReadOnlyList<string> EnabledCapabilities,
    IReadOnlyList<string> DisabledCapabilities,
    string LimitsJson,
    IReadOnlyList<string> ProviderStatuses,
    string CapturedAtUtc);

public sealed record ResourceSnapshot(
    double? CpuBudget,
    double? MemoryBudget,
    double? StorageBudget,
    int QueueDepth,
    double ModelBudget,
    string NetworkStatus,
    string? MaintenanceWindow,
    string CapturedAtUtc);

/// <summary>
/// What Aurora currently is, observed rather than assumed (RFC 027).
/// </summary>
/// <remarks>
/// Not consciousness and not authority. It is the answer to "what can you actually do right now",
/// which an agent that cannot answer will replace with promises it cannot keep.
/// </remarks>
public sealed record SelfModel(
    string Id,
    string MindId,
    int Version,
    string IdentityRef,
    string? PersonalityRef,
    CapabilitySnapshot Capabilities,
    ResourceSnapshot Resources,
    string OperationalState,
    IReadOnlyList<string> ActiveCycleIds,
    string? CurrentFocusRef,
    string HealthSummary,
    /// <summary>When the health behind this was observed. Rule 4: dated, and revocable.</summary>
    string HealthObservedAtUtc,
    IReadOnlyList<string> RecentActivityRefs,
    string ObservedAtUtc,
    string? PausedReason = null);

/// <summary>
/// What Aurora will say about itself to someone who asked (RFC 027 rule 3).
/// </summary>
/// <remarks>
/// A separate type rather than a filtered <see cref="SelfModel"/>, because filtering is something
/// somebody forgets. There is no field here that could carry a secret, a hostname or a credential
/// identifier — not because they are stripped, but because there is nowhere to put them.
/// </remarks>
public sealed record SafeSelfDescription(
    string OperationalState,
    IReadOnlyList<string> CanDo,
    IReadOnlyList<string> CannotDo,
    string HealthSummary,
    string HealthObservedAtUtc,
    int ActiveCycles,
    string ObservedAtUtc);

/// <summary>
/// Three separate answers about one capability (RFC 027 rule 2).
/// </summary>
/// <remarks>
/// Installed, permitted and safe-right-now are different questions and none implies another. A
/// connector can be installed and revoked; permitted and out of budget; safe and not installed.
/// Collapsing them into one boolean is how "I can do that" becomes a promise Aurora cannot keep —
/// and it is why this type has three fields rather than one.
/// </remarks>
public sealed record CapabilityAssessment(
    string ActionId,
    bool Installed,
    bool Permitted,
    bool SafeNow,
    string Reason)
{
    /// <summary>All three, which is the only combination that means "yes".</summary>
    public bool Available => Installed && Permitted && SafeNow;
}

public sealed class SelfException : Exception
{
    public SelfException(string message) : base(message)
    {
    }
}
