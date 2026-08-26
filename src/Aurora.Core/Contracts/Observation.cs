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

public static class LearningRisk
{
    /// <summary>
    /// The only risk at which RFC 08 rule 2 allows a change to be committed without a human.
    /// </summary>
    public const string Low = "LOW";

    public const string Medium = "MEDIUM";
    public const string High = "HIGH";
}

/// <summary>The four kinds of change a reflection may propose (RFC 08).</summary>
/// <remarks>
/// Only <see cref="Memory"/> is ever eligible for automatic application, and only at
/// <see cref="LearningRisk.Low"/>. The other three are the ones rule 3 names — personality,
/// policy, tools, templates, automation — and each of them must be approved, tested and
/// reversible before it takes effect.
/// </remarks>
public static class LearningProposalType
{
    public const string Memory = "MEMORY";
    public const string Procedure = "PROCEDURE";
    public const string PromptConfig = "PROMPT_CONFIG";
    public const string PolicySuggestion = "POLICY_SUGGESTION";
}

public sealed record LearningProposal(
    string Id,
    string ReflectionId,
    string Type,
    string ChangeSetJson,
    string EvaluationPlan,
    string RollbackPlan,
    string State,
    /// <summary>What this change is expected to improve, in the proposer's own words.</summary>
    string ExpectedBenefit = "",
    /// <summary>One of <see cref="LearningRisk"/>. Unset reads as the most cautious answer.</summary>
    string Risk = LearningRisk.High,
    /// <summary>
    /// What this proposal rests on. RFC 08 rule 1: a reflection cites concrete evidence, and a
    /// proposal that inherits none of it is a suggestion with nothing behind it.
    /// </summary>
    IReadOnlyList<string>? EvidenceRefs = null)
{
    public IReadOnlyList<string> EvidenceRefs { get; init; } = EvidenceRefs ?? [];
}

/// <summary>What an evaluation concluded (RFC 08).</summary>
public static class EvaluationVerdict
{
    /// <summary>Every dimension the RFC mandates was measured, and none regressed.</summary>
    public const string Pass = "PASS";

    /// <summary>At least one mandated dimension regressed. The proposal does not go on.</summary>
    public const string Fail = "FAIL";

    /// <summary>
    /// Something the RFC mandates could not be measured, or moved both ways.
    /// </summary>
    /// <remarks>
    /// RFC 08's limit case, and the default rather than the exception: a metric that improves in
    /// one place and worsens in another "keeps in test and requires human decision". So does one
    /// Aurora had no way to measure at all. A verdict of PASS means something was checked; it is
    /// never what an evaluator says when it did not look.
    /// </remarks>
    public const string Inconclusive = "INCONCLUSIVE";
}

/// <summary>
/// One evaluation of one proposal, against one scope (RFC 08).
/// </summary>
/// <param name="DatasetRef">What the evaluation ran against — here, the evidence it was given.</param>
/// <param name="MetricsJson">
/// Every dimension the RFC mandates, each with the value and whether it could be measured at all.
/// A dimension Aurora cannot measure is written as unmeasured, with the reason, and never as a
/// zero: "I did not look" and "I looked and found nothing" call for opposite responses.
/// </param>
public sealed record EvaluationRun(
    string Id,
    string ProposalId,
    string TestScope,
    string DatasetRef,
    string MetricsJson,
    string Verdict,
    string ExecutedAtUtc);

public sealed class ObservationException : Exception
{
    public ObservationException(string message) : base(message)
    {
    }
}
