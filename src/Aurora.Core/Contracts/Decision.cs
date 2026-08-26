namespace Aurora.Core.Contracts;

/// <summary>What the engine decided to do (RFC 022).</summary>
public static class DecisionMode
{
    public const string Respond = "RESPOND";
    public const string Ask = "ASK";
    public const string CreateGoal = "CREATE_GOAL";
    public const string Plan = "PLAN";
    public const string Wait = "WAIT";
    public const string ToolCall = "TOOL_CALL";
    public const string Refuse = "REFUSE";
    public const string Silent = "SILENT";

    /// <summary>Modes whose effect reaches outside Aurora.</summary>
    /// <remarks>
    /// Compared rather than pattern-matched: RFC 06 also names a <c>ToolCall</c> record, and
    /// <c>is</c> would resolve the identifier to that type instead of to this constant.
    /// </remarks>
    public static bool HasExternalEffect(string mode) =>
        string.Equals(mode, ToolCall, StringComparison.Ordinal);
}

public static class DecisionState
{
    public const string Proposed = "PROPOSED";
    public const string Committed = "COMMITTED";
    public const string Executed = "EXECUTED";
    public const string Superseded = "SUPERSEDED";
    public const string Expired = "EXPIRED";
}

/// <summary>
/// The only reasons silence is permitted (RFC 022 rule 3).
/// </summary>
/// <remarks>
/// There is deliberately no value for hiding a failure. Making the reason a closed set means
/// silence has to be justified by one of the four the RFC allows, and a caller cannot invent a
/// fifth without changing this file and explaining why.
/// </remarks>
public static class SilenceReason
{
    public const string ChannelRule = "CHANNEL_RULE";
    public const string Privacy = "PRIVACY";
    public const string NoiseLimit = "NOISE_LIMIT";
    public const string NoRecipient = "NO_RECIPIENT";

    public static bool IsAllowed(string? reason) =>
        reason is ChannelRule or Privacy or NoiseLimit or NoRecipient;
}

/// <summary>
/// The six axes RFC 022 rule 1 requires. Required by construction, so an option cannot exist
/// without having been evaluated on all of them.
/// </summary>
public sealed record OptionEvaluation(
    double Relevance,
    bool HasEvidence,
    string RiskLevel,
    double CostEstimate,
    bool Permitted,
    bool Reversible);

public sealed record DecisionOption(
    string Mode,
    string RationaleSummary,
    IReadOnlyList<string> ExpectedEffects,
    OptionEvaluation Evaluation,
    IReadOnlyList<string> Prerequisites,
    IReadOnlyList<string> BlockingReasons,
    string? SilenceReasonCode = null);

public sealed record Decision(
    string Id,
    string CycleId,
    string Mode,
    string? ObjectiveRef,
    DecisionOption SelectedOption,
    IReadOnlyList<DecisionOption> AlternativesConsidered,
    IReadOnlyList<string> EvidenceRefs,
    IReadOnlyList<string> Uncertainty,
    string RiskLevel,
    double Confidence,
    IReadOnlyList<string> PolicyDecisionIds,
    bool ApprovalRequired,
    string? ExpiryAtUtc,
    string Status,
    /// <summary>
    /// The constitutional assessment this decision was committed against (RFC 035 rule 2).
    /// </summary>
    /// <remarks>
    /// Present on a high-risk decision and null on the rest — not because the Articles stop
    /// applying, but because a decision that changes nothing outside Aurora does not have to carry
    /// the paperwork proving it.
    /// </remarks>
    string? ConstitutionalAssessmentRef = null);

/// <summary>What the cycle brings to the engine.</summary>
public sealed record DecisionThought(
    string CycleId,
    string? ObjectiveRef,
    IReadOnlyList<DecisionOption> Options,
    IReadOnlyList<string> EvidenceRefs,
    double Confidence,
    string RiskLevel,
    /// <summary>Set when the cycle is reporting a failure. Silence is then never available.</summary>
    bool ReportingFailure = false);

/// <summary>The state the decision is being made in.</summary>
public sealed record DecisionContext(
    bool MotorAvailable,
    IReadOnlyList<string> AllowedSilenceReasons,
    string? DeadlineAtUtc = null);

/// <summary>The outcome of the Policy stage, handed back to commit a decision.</summary>
public sealed record PolicyResult(
    string PolicyDecisionId, bool Allowed, bool ApprovalSatisfied, string? Reason = null);

public sealed class DecisionException : Exception
{
    public DecisionException(string message) : base(message)
    {
    }
}
