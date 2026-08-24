namespace Aurora.Core.Contracts;

public static class WorldAssertionStatus
{
    /// <summary>Observed but not validated. RFC 041 rule 5: a tool may create these and nothing more.</summary>
    public const string Proposed = "PROPOSED";

    public const string Current = "CURRENT";
    public const string Historical = "HISTORICAL";
    public const string Disputed = "DISPUTED";
    public const string Retracted = "RETRACTED";

    /// <summary>The external thing is gone; the evidence about it is not.</summary>
    public const string Inaccessible = "INACCESSIBLE";
}

/// <summary>
/// What kind of claim a predicate makes (RFC 041 rule 3).
/// </summary>
/// <remarks>
/// Ownership, social participation, access and permission are kept as separate categories because
/// the RFC is explicit that they are distinct objects: "the person has Discord" does not imply that
/// Aurora can read that Discord. Categories are what stop one being read as evidence for another.
/// </remarks>
public static class WorldPredicateCategory
{
    public const string Ownership = "OWNERSHIP";
    public const string Social = "SOCIAL";
    public const string Access = "ACCESS";
    public const string Permission = "PERMISSION";
    public const string Attribute = "ATTRIBUTE";
}

/// <summary>A temporal, evidenced claim about operational reality (RFC 041).</summary>
public sealed record WorldAssertion(
    string Id,
    string SubjectRef,
    string Predicate,
    string Category,
    string? ObjectRef,
    string? Literal,
    IReadOnlyList<string> EvidenceRefs,
    double Confidence,
    string ValidFromUtc,
    string? ValidToUtc,
    string ObservedAtUtc,
    string AssertedAtUtc,
    string Status,
    string VersionId);

public static class ResolutionDecision
{
    public const string Match = "MATCH";
    public const string Create = "CREATE";

    /// <summary>Not enough evidence to decide. Deferring is a real answer, not a failure.</summary>
    public const string Defer = "DEFER";
}

/// <summary>The record of an identity decision (RFC 041 rule 2).</summary>
public sealed record EntityResolution(
    string CandidateRef,
    string ObservedName,
    double MatchScore,
    IReadOnlyList<string> EvidenceRefs,
    string Decision,
    string DecidedBy,
    string DecidedAtUtc,
    string? MatchedEntityRef = null);

public static class WorldVersionStatus
{
    /// <summary>Not a source for decisions until validation completes.</summary>
    public const string Draft = "DRAFT";

    public const string Active = "ACTIVE";
    public const string RolledBack = "ROLLED_BACK";
}

public sealed record WorldModelVersion(
    string Id, string MindId, string? ParentVersionId, string Status, string CreatedAtUtc);

/// <summary>
/// What the World Model knows about a question (RFC 041 rule 4).
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> exists so that "we have no edge" can never be reported as "there is no
/// such thing in the world". The absence of a record is a fact about Aurora, not about reality.
/// </remarks>
public enum WorldKnowledge
{
    Unknown,
    Asserted,
    OnlyHistorical,
    Disputed,
}

public sealed record WorldAnswer(
    WorldKnowledge Knowledge, IReadOnlyList<WorldAssertion> Assertions, string Explanation);

public sealed class WorldModelException : Exception
{
    public WorldModelException(string message) : base(message)
    {
    }
}
