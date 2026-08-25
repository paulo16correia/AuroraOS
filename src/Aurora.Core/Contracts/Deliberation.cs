namespace Aurora.Core.Contracts;

/// <summary>The ordered phases a deliberation moves through (RFC 025).</summary>
public static class DeliberationPhase
{
    public const string Orient = "ORIENT";
    public const string Retrieve = "RETRIEVE";
    public const string Compare = "COMPARE";
    public const string Plan = "PLAN";
    public const string Decide = "DECIDE";
    public const string Verify = "VERIFY";

    public static readonly IReadOnlyList<string> Order =
        [Orient, Retrieve, Compare, Plan, Decide, Verify];

    public static int IndexOf(string phase) => Order.ToList().IndexOf(phase);

    public static bool IsKnown(string phase) => IndexOf(phase) >= 0;
}

public static class DeliberationStatus
{
    public const string Active = "ACTIVE";
    public const string Paused = "PAUSED";
    public const string Closed = "CLOSED";
}

/// <summary>How a deliberation ended. A closed set, so "it just stopped" is not one of them.</summary>
public static class DeliberationDisposition
{
    public const string Concluded = "CONCLUDED";

    /// <summary>Ran out of what it needed. RFC 025 requires this to carry concrete questions.</summary>
    public const string Inconclusive = "INCONCLUSIVE";

    public const string Superseded = "SUPERSEDED";
    public const string Expired = "EXPIRED";

    public static bool IsKnown(string disposition) =>
        disposition is Concluded or Inconclusive or Superseded or Expired;
}

public static class ThoughtStatus
{
    public const string Draft = "DRAFT";
    public const string Validated = "VALIDATED";
    public const string Rejected = "REJECTED";
    public const string Superseded = "SUPERSEDED";
}

/// <summary>
/// Something the deliberation holds to be the case, and what makes it so (RFC 025 rule 2).
/// </summary>
/// <remarks>
/// <see cref="EvidenceRefs"/> being empty is not an error — it is what makes this a hypothesis
/// rather than a finding, and <see cref="IsHypothesis"/> says so out loud. The rule is that a claim
/// without evidence stays a hypothesis, not that it may not be made.
/// </remarks>
public sealed record Assertion(
    string Claim,
    IReadOnlyList<string> EvidenceRefs,
    double Confidence)
{
    public bool IsHypothesis => EvidenceRefs.Count == 0;
}

/// <summary>
/// The internal working state of one deliberation (RFC 025).
/// </summary>
/// <remarks>
/// Bound to a cycle and to a deadline, both required: rule 1 forbids an ownerless global mental
/// process, and a deliberation with no end is exactly that.
/// </remarks>
public sealed record DeliberationState(
    string Id,
    string CycleId,
    string Phase,
    string ActiveQuestion,
    IReadOnlyList<string> UnresolvedQuestions,
    IReadOnlyList<string> CandidateRefs,
    IReadOnlyList<Assertion> Assertions,
    IReadOnlyList<string> Uncertainty,
    string? NextStep,
    string Status,
    /// <summary>
    /// Where the protected technical material sits. A locator, never the material.
    /// </summary>
    /// <remarks>
    /// Rule 4: the trace is minimised, encrypted and kept out of normal exports. Nothing that
    /// returns a <see cref="DeliberationState"/> returns the trace itself, and nothing outside
    /// operational access can ask for it.
    /// </remarks>
    string? TraceRef,
    string RetentionUntilUtc,
    string StartedAtUtc,
    string DeadlineAtUtc);

/// <summary>What one step of deliberation contributes.</summary>
public sealed record DeliberationStep(
    IReadOnlyList<Assertion>? Assertions = null,
    IReadOnlyList<string>? CandidateRefs = null,
    IReadOnlyList<string>? Uncertainty = null,
    IReadOnlyList<string>? ResolvedQuestions = null,
    IReadOnlyList<string>? NewQuestions = null,
    string? NextStep = null,
    /// <summary>
    /// Working notes. Encrypted at rest, retained briefly, and never returned to a caller.
    /// </summary>
    string? Trace = null);

/// <summary>
/// The explainable output of a deliberation (RFC 025).
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="DeliberationState"/>. The state is how Aurora worked; this
/// is what it can say about it. Separating them is what lets the second be shared without the
/// first, which is the privacy argument the RFC makes and also the honest one — a transcript of
/// intermediate reasoning is not an explanation.
/// </remarks>
public sealed record Thought(
    string Id,
    string CycleId,
    string DeliberationId,
    string Intent,
    string? ObjectiveRef,
    IReadOnlyList<string> EvidenceRefs,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Options,
    IReadOnlyList<string> Uncertainty,
    string RecommendedOption,
    /// <summary>
    /// Reason, sources, and what happens next. Not a transcript of the reasoning.
    /// </summary>
    /// <remarks>
    /// Composed from those three parts rather than written freely, because rule 3 forbids Aurora
    /// from offering "I am thinking" as evidence of work — and a free-form field is where that
    /// sentence gets in.
    /// </remarks>
    string UserExplanation,
    string Status,
    string CreatedAtUtc);

/// <summary>The parts a user-facing explanation is built from.</summary>
public sealed record ThoughtRequest(
    string Intent,
    string RecommendedOption,
    IReadOnlyList<string> Options,
    string? ObjectiveRef = null,
    IReadOnlyList<string>? Assumptions = null);

public sealed class DeliberationException : Exception
{
    public DeliberationException(string message) : base(message)
    {
    }
}
