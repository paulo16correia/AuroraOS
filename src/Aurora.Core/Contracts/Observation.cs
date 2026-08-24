namespace Aurora.Core.Contracts;

/// <summary>Lifecycle of an action (RFC 040).</summary>
public static class ActionState
{
    public const string Proposed = "PROPOSED";
    public const string Authorized = "AUTHORIZED";
    public const string Dispatched = "DISPATCHED";

    /// <summary>Reached only with an observation attached (LAW-003).</summary>
    public const string Observed = "OBSERVED";

    public const string Cancelled = "CANCELLED";
    public const string Unknown = "UNKNOWN";
}

/// <summary>Lifecycle of an observation (RFC 040).</summary>
public static class ObservationState
{
    /// <summary>As received. Untrusted until validated.</summary>
    public const string Raw = "RAW";

    public const string Validated = "VALIDATED";
    public const string Rejected = "REJECTED";
    public const string Consolidated = "CONSOLIDATED";
    public const string Expired = "EXPIRED";
}

/// <summary>What an observation reports about the action it closes.</summary>
public static class ObservationOutcome
{
    public const string Success = "SUCCESS";
    public const string Failure = "FAILURE";
    public const string Abort = "ABORT";

    /// <summary>We never learned. LAW-003 requires this rather than a presumed success.</summary>
    public const string Unknown = "UNKNOWN";

    public static bool IsKnown(string outcome) =>
        outcome is Success or Failure or Abort or Unknown;
}

public sealed record AuroraAction(
    string Id,
    string DecisionId,
    string EffectType,
    string TargetRef,
    string ParametersHash,
    bool Reversible,
    string State,
    string? ToolCallId = null);

public sealed record Observation(
    string Id,
    string ActionId,
    string Observer,
    string ObservedAtUtc,
    string Modality,
    string Outcome,
    string? PayloadRef,
    string Integrity,
    string? ExternalRef,
    string State,
    string? RejectionReason = null);

public static class ReflectionState
{
    public const string Draft = "DRAFT";
    public const string Accepted = "ACCEPTED";
    public const string Rejected = "REJECTED";
    public const string Implemented = "IMPLEMENTED";
    public const string Expired = "EXPIRED";
}

/// <summary>
/// What was made of an observation (RFC 040).
/// </summary>
/// <remarks>
/// A reflection with no lessons is still a reflection. RFC 021 rule 5 requires one after every
/// execution "even when reflection concludes no learning", because a system that only records
/// interesting outcomes has no baseline to compare them against.
/// </remarks>
public sealed record Reflection(
    string Id,
    string ObservationId,
    string Outcome,
    IReadOnlyList<string> EvidenceRefs,
    IReadOnlyList<string> Lessons,
    IReadOnlyList<string> ProposalRefs,
    string State);

public static class LearningProposalState
{
    public const string Proposed = "PROPOSED";
    public const string Approved = "APPROVED";
    public const string Testing = "TESTING";
    public const string Deployed = "DEPLOYED";
    public const string Rejected = "REJECTED";
    public const string RolledBack = "ROLLED_BACK";
}

public sealed record LearningProposal(
    string Id,
    string ReflectionId,
    string Type,
    string ChangeSetJson,
    string EvaluationPlan,
    string RollbackPlan,
    string State);

public sealed class ObservationException : Exception
{
    public ObservationException(string message) : base(message)
    {
    }
}
