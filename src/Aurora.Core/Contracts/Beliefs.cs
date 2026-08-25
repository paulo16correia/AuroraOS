namespace Aurora.Core.Contracts;

/// <summary>Where a belief came from (RFC 028).</summary>
public static class BeliefBasis
{
    /// <summary>Seen happen, more than once.</summary>
    public const string Observed = "OBSERVED";

    /// <summary>Reasoned to. Never sufficient on its own — the model is not evidence.</summary>
    public const string Inferred = "INFERRED";

    /// <summary>The person said so. Outranks inference, and stays correctable.</summary>
    public const string UserStated = "USER_STATED";

    public const string Imported = "IMPORTED";

    public static bool IsKnown(string basis) =>
        basis is Observed or Inferred or UserStated or Imported;
}

public static class BeliefStatus
{
    /// <summary>Not yet supported enough to act on. Nothing material is personalised from these.</summary>
    public const string Candidate = "CANDIDATE";

    public const string Active = "ACTIVE";

    /// <summary>Contradicted. Still on record, and no longer usable as support.</summary>
    public const string Challenged = "CHALLENGED";

    public const string Superseded = "SUPERSEDED";
    public const string Retracted = "RETRACTED";
    public const string Expired = "EXPIRED";

    /// <summary>The two states a belief may be leant on in.</summary>
    public static bool IsUsable(string status) => status is Active;
}

public static class DecisionImpact
{
    public const string Low = "LOW";
    public const string Medium = "MEDIUM";
    public const string High = "HIGH";

    public static bool IsKnown(string impact) => impact is Low or Medium or High;
}

/// <summary>
/// The domains where a belief may never be the only reason (RFC 028 rule 2).
/// </summary>
/// <remarks>
/// A closed set, taken from the rule word for word. Closed because the interesting failure is not
/// misjudging one of these — it is a purpose nobody classified quietly getting the benefit of the
/// doubt. Anything not on the list is ordinary; anything on it needs something better than a
/// pattern Aurora noticed.
/// </remarks>
public static class BeliefPurpose
{
    public const string Ordinary = "ORDINARY";

    public const string Identity = "IDENTITY";
    public const string Security = "SECURITY";
    public const string Money = "MONEY";
    public const string Health = "HEALTH";
    public const string Law = "LAW";
    public const string SensitiveContent = "SENSITIVE_CONTENT";

    public static bool IsHighRisk(string purpose) =>
        purpose is Identity or Security or Money or Health or Law or SensitiveContent;

    public static bool IsKnown(string purpose) => purpose == Ordinary || IsHighRisk(purpose);
}

/// <summary>
/// A reviewable generalisation that guides attention, and is not a fact (RFC 028).
/// </summary>
/// <remarks>
/// Kept apart from memory on purpose. A memory is something that was recorded; a belief is a
/// pattern Aurora thinks it sees in them. Separating the two is what lets Aurora use a useful
/// pattern without pretending it is reality, and stops a transitory inference hardening into a
/// permanent fact.
/// </remarks>
public sealed record Belief(
    string Id,
    string SubjectRef,
    string Predicate,
    string ObjectJson,
    /// <summary>Where this is held to apply. Narrowing it is how a contradiction is answered.</summary>
    string ScopeJson,
    double Confidence,
    IReadOnlyList<string> EvidenceForRefs,
    IReadOnlyList<string> EvidenceAgainstRefs,
    string Basis,
    string Status,
    string ValidFromUtc,
    string ReviewAtUtc,
    string LastEvaluatedAtUtc,
    string DecisionImpact);

/// <summary>One observation applied to a belief. Kept, so a wrong prediction is not erased.</summary>
public sealed record BeliefUpdate(
    string Id,
    string BeliefId,
    string ObservationRef,
    double DeltaConfidence,
    string Reason,
    string AppliedAtUtc);

/// <summary>What a caller proposes to believe.</summary>
public sealed record BeliefCandidate(
    string SubjectRef,
    string Predicate,
    string ObjectJson,
    string Basis,
    double Confidence,
    string ScopeJson = "{}",
    string DecisionImpact = Contracts.DecisionImpact.Low);

/// <summary>
/// Beliefs offered as support, and whether they may carry the decision alone.
/// </summary>
/// <remarks>
/// The second field is the point. A caller cannot receive beliefs for a high-risk purpose without
/// also receiving the answer that they are not enough — the two travel together so the check
/// cannot be skipped by not making it.
/// </remarks>
public sealed record BeliefSupport(
    IReadOnlyList<Belief> Beliefs,
    bool MayBeSoleBasis,
    string Reason);

/// <summary>How beliefs age when nothing confirms them (RFC 028 rule 3).</summary>
public sealed record BeliefPolicy(
    TimeSpan ReviewAfter,
    TimeSpan ConfidenceHalfLife,
    double CandidateThreshold,
    double MinimumUsableConfidence)
{
    /// <summary>
    /// Reviewed monthly, halving weekly, usable above 0.6.
    /// </summary>
    /// <remarks>
    /// The half-life is the explicit policy rule 3 asks for. A pattern nobody has seen again in a
    /// week is, by observation, weaker than it was — and a belief that never weakened would be a
    /// fact, which is the confusion this whole system exists to prevent.
    /// </remarks>
    public static BeliefPolicy Default { get; } = new(
        ReviewAfter: TimeSpan.FromDays(30),
        ConfidenceHalfLife: TimeSpan.FromDays(7),
        CandidateThreshold: 0.5,
        MinimumUsableConfidence: 0.6);
}

public sealed class BeliefException : Exception
{
    public BeliefException(string message) : base(message)
    {
    }
}
