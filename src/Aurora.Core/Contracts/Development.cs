namespace Aurora.Core.Contracts;

public static class DevelopmentStatus
{
    /// <summary>Everything is confirmed, including what policy would let through.</summary>
    public const string Probation = "PROBATION";

    public const string Active = "ACTIVE";

    /// <summary>Pulled back after an incident, in the scope the incident touched.</summary>
    public const string Restricted = "RESTRICTED";

    public const string Paused = "PAUSED";

    public static bool IsKnown(string status) =>
        status is Probation or Active or Restricted or Paused;
}

/// <summary>
/// Evidence that Aurora is reliable at one level of risk.
/// </summary>
/// <remarks>
/// Scoped to a risk level on purpose, and it is the whole answer to RFC 037's sharpest limit case:
/// many low-risk successes do not justify financial autonomy, SSH or public communication. Evidence
/// at LOW cannot satisfy a criterion at MEDIUM, because they are counted separately and never
/// added up.
/// </remarks>
public sealed record PromotionCriterion(
    RiskLevel Risk,
    int MinimumSuccesses,
    int MaximumFailures);

/// <summary>
/// One step of operational maturity.
/// </summary>
/// <remarks>
/// <see cref="AutonomyCeiling"/> is the highest risk this stage will let run without development
/// asking first — and it can only ever be at or below what policy already allows. A stage never
/// grants anything; it decides how much of Aurora's own caution to keep on top of the rules.
/// </remarks>
public sealed record DevelopmentStage(
    string Id,
    string Name,
    RiskLevel AutonomyCeiling,
    IReadOnlyList<string> ConfirmationRules,
    IReadOnlyList<string> CapabilityConstraints,
    IReadOnlyList<PromotionCriterion> PromotionCriteria,
    IReadOnlyList<string> RegressionCriteria);

public sealed record DevelopmentProfile(
    string Id, string GenomeRef, IReadOnlyList<DevelopmentStage> Stages);

public sealed record DevelopmentState(
    string MindId,
    string CurrentStageId,
    IReadOnlyList<string> EvidenceRefs,
    string AssessmentAtUtc,
    string Status,
    /// <summary>The scope an incident pulled back, when the status is RESTRICTED.</summary>
    IReadOnlyList<string> RestrictedScopes,
    string? Reason = null);

/// <summary>What the evidence actually shows, per risk level.</summary>
public sealed record ReliabilityEvidence(
    RiskLevel Risk, int Successes, int Failures, IReadOnlyList<string> Refs);

/// <summary>
/// Whether the evidence supports moving on, and what is missing if it does not.
/// </summary>
/// <remarks>
/// <see cref="Missing"/> is the useful half. "Not yet" is not an answer somebody can act on; "four
/// more successful MEDIUM actions and no further failures" is.
/// </remarks>
public sealed record DevelopmentAssessment(
    string MindId,
    string CurrentStageId,
    string? NextStageId,
    IReadOnlyList<ReliabilityEvidence> Evidence,
    bool ReadyToPromote,
    IReadOnlyList<string> Missing,
    string AssessedAtUtc);

public sealed record DevelopmentProposal(
    string Id,
    string MindId,
    string FromStageId,
    string ToStageId,
    IReadOnlyList<string> EvidenceRefs,
    string Rationale,
    string ProposedAtUtc,
    string Status,
    string? ApprovalRef = null);

public static class ProposalStatus
{
    public const string Proposed = "PROPOSED";
    public const string Applied = "APPLIED";
    public const string Rejected = "REJECTED";
    public const string Withdrawn = "WITHDRAWN";
}

public sealed class DevelopmentException : Exception
{
    public DevelopmentException(string message) : base(message)
    {
    }
}
