namespace Aurora.Core.Contracts;

/// <summary>The Mind's own life cycle (RFC 020).</summary>
public static class MindStatus
{
    public const string Initializing = "INITIALIZING";
    public const string Active = "ACTIVE";
    public const string Degraded = "DEGRADED";

    /// <summary>
    /// Nothing starts on its own. Inspection and authorized export still work — rule 3 is a
    /// restriction on acting, not a blackout.
    /// </summary>
    public const string Paused = "PAUSED";

    public const string Recovering = "RECOVERING";
    public const string Retired = "RETIRED";
}

/// <summary>Who asked for a change (RFC 020).</summary>
public static class MindChangeSource
{
    public const string User = "USER";
    public const string Cycle = "CYCLE";
    public const string Tool = "TOOL";
    public const string Scheduler = "SCHEDULER";
    public const string Operator = "OPERATOR";

    public static bool IsKnown(string source) =>
        source is User or Cycle or Tool or Scheduler or Operator;
}

public static class MindChangeSetStatus
{
    public const string Proposed = "PROPOSED";
    public const string Validated = "VALIDATED";
    public const string Applied = "APPLIED";
    public const string Rejected = "REJECTED";

    /// <summary>
    /// Applied in part and then undone.
    /// </summary>
    /// <remarks>
    /// Rule 2's "never partially silent". A change set that half-applied and stopped would leave
    /// the Mind in a shape nobody proposed, so the failure is undone and said out loud instead.
    /// </remarks>
    public const string RolledBack = "ROLLED_BACK";
}

/// <summary>The fields of the Mind a change set may move.</summary>
/// <remarks>
/// Deliberately few. Memories, beliefs, relationships and goals are owned by their own services and
/// have their own provenance-enforced write paths under LAW-001; routing them through here would be
/// a second way in, and a second way in is the thing LAW-001 exists to prevent. What is left is
/// what the Mind aggregate itself owns: which self model and identity are current, which policy and
/// world versions are in force, and when consolidation last ran.
/// </remarks>
public static class MindField
{
    public const string SelfModelId = "self_model_id";
    public const string IdentityId = "identity_id";
    public const string PolicySetVersion = "policy_set_version";
    public const string WorldModelVersion = "world_model_version";
    public const string LastConsolidationAt = "last_consolidation_at";

    public static bool IsKnown(string field) =>
        field is SelfModelId or IdentityId or PolicySetVersion or WorldModelVersion
            or LastConsolidationAt;
}

/// <summary>One field moving from whatever it was to something named.</summary>
public sealed record MindChange(string Field, string Value);

/// <summary>
/// A proposed change to the Mind, and the discipline it goes through (RFC 020).
/// </summary>
/// <remarks>
/// Proposed, then validated, then applied — three steps rather than a setter, because rule 1 says
/// nothing writes to Mind directly and a setter is exactly that.
/// </remarks>
public sealed record MindChangeSet(
    string Id,
    string MindId,
    string Source,
    IReadOnlyList<MindChange> Changes,
    IReadOnlyList<string> EvidenceRefs,
    IReadOnlyList<string> PolicyDecisionIds,
    string Status,
    string CreatedAtUtc,
    string? Detail = null);

/// <summary>
/// The aggregate Aurora's persistent state belongs to (RFC 020).
/// </summary>
/// <remarks>
/// The lists RFC 020 names — beliefs, preferences, relationships, active goals and tasks — are not
/// stored here. Each is owned by the service that writes it, and copying the ids onto this record
/// would create a second answer to "what is active" that goes stale the moment either side changes.
/// This holds what nothing else owns.
/// </remarks>
public sealed record Mind(
    string Id,
    string TenantId,
    string Status,
    string? SelfModelId,
    string? IdentityId,
    string PolicySetVersion,
    string WorldModelVersion,
    string? LastConsolidationAtUtc,
    string CreatedAtUtc,
    string UpdatedAtUtc,
    string? PausedBy = null,
    string? PausedReason = null);

public sealed class MindException : Exception
{
    public MindException(string message) : base(message)
    {
    }
}
