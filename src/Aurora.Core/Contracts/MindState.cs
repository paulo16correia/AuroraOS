namespace Aurora.Core.Contracts;

/// <summary>Lifecycle of a snapshot (RFC 043).</summary>
public static class SnapshotStatus
{
    public const string Creating = "CREATING";
    public const string Complete = "COMPLETE";
    public const string Verified = "VERIFIED";
    public const string Restored = "RESTORED";
    public const string Expired = "EXPIRED";
    public const string Corrupt = "CORRUPT";
}

/// <summary>How much consistency the capture claims (RFC 043 rule 1).</summary>
public enum ConsistencyLevel
{
    /// <summary>Every component must report a version, or the capture fails.</summary>
    Strict,

    /// <summary>Components without a version are captured and named as non-consistent.</summary>
    BestEffort,
}

/// <summary>
/// The references that make up a Mind at one instant (RFC 043).
/// </summary>
/// <remarks>
/// References only. `tool_state_refs` carry identifiers and states, never secrets, and nothing here
/// holds a value from the Vault.
/// </remarks>
public sealed record MindStateComponents(
    string IdentityRef,
    string PersonalityRef,
    string SelfRef,
    IReadOnlyList<string> BeliefRefs,
    IReadOnlyList<string> PreferenceRefs,
    IReadOnlyList<string> RelationshipRefs,
    IReadOnlyList<string> GoalRefs,
    IReadOnlyList<string> ActiveTaskRefs,
    IReadOnlyList<string> PlanRefs,
    string AttentionStateRef,
    IReadOnlyList<string> WorkingMemoryRefs,
    string WorldModelVersion,
    IReadOnlyList<string> ToolStateRefs,
    IReadOnlyList<string> SchedulerStateRefs,
    string InteractionStateRef,
    string PolicySetVersion,
    string HealthRef,
    string EffectiveGenomeRef,
    IReadOnlyList<string> NonConsistentComponents);

/// <summary>A captured Mind State (RFC 043).</summary>
public sealed record MindStateSnapshot(
    string Id,
    string MindId,
    int SchemaVersion,
    string CapturedAtUtc,
    string ConsistencyCursor,
    string? AuditAnchorHash,
    string EncryptionMetadata,
    string Status,
    IReadOnlyList<string> NonConsistentComponents);

public static class RecoveryStatus
{
    public const string Planned = "PLANNED";
    public const string Running = "RUNNING";
    public const string WaitingReconciliation = "WAITING_RECONCILIATION";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
}

/// <summary>The ordered work a restore performs (RFC 043).</summary>
public sealed record RecoveryPlan(
    string Id,
    string SnapshotId,
    string TargetEnvironment,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> UnresolvedToolCallRefs,
    string ReconciliationPolicy,
    string Status);

public sealed record VerificationReport(string SnapshotId, string Status, string? Detail = null);

/// <summary>An export for a person, with everything policy forbids stripped out (RFC 043 rule 4).</summary>
public sealed record RedactedExport(
    string SnapshotId,
    string MindId,
    string CapturedAtUtc,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Sections,
    IReadOnlyList<string> RedactedSections);

public sealed class MindStateException : Exception
{
    public MindStateException(string message) : base(message)
    {
    }
}
