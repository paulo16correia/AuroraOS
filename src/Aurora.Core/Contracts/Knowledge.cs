namespace Aurora.Core.Contracts;

public static class EntityStatus
{
    public const string Active = "ACTIVE";
    public const string Merged = "MERGED";
    public const string Archived = "ARCHIVED";
}

public static class RelationStatus
{
    /// <summary>No source, or a source that is not an active memory. Never the sole basis of an action.</summary>
    public const string Proposed = "PROPOSED";

    public const string Asserted = "ASSERTED";
    public const string Disputed = "DISPUTED";
    public const string Retracted = "RETRACTED";
}

public static class Cardinality
{
    public const string One = "ONE";
    public const string Many = "MANY";
}

/// <summary>A node in the graph (RFC 04).</summary>
public sealed record KnowledgeEntity(
    string Id,
    string Type,
    string CanonicalName,
    IReadOnlyList<string> Aliases,
    string AttributesJson,
    string Status,
    string SensitivityClass,
    IReadOnlyList<string> SourceRefs,
    string? MergedIntoId = null);

/// <summary>A typed, temporal edge (RFC 04).</summary>
public sealed record KnowledgeRelation(
    string Id,
    string SubjectId,
    string Predicate,
    string? ObjectId,
    string? LiteralJson,
    string? QualifierJson,
    double Confidence,
    IReadOnlyList<string> SourceMemoryIds,
    string Status,
    string? ValidFromUtc,
    string? ValidToUtc,
    string AssertedAtUtc);

/// <summary>
/// The schema a predicate must satisfy (RFC 04 rule 1).
/// </summary>
/// <remarks>
/// Rule 1 forbids free relations in the canonical graph. A predicate that is not registered here
/// simply cannot be asserted, which is what stops language-generated phrases becoming facts.
/// </remarks>
public sealed record PredicateSchema(
    string Key,
    string DisplayName,
    IReadOnlyList<string> AllowedSubjectTypes,
    IReadOnlyList<string> AllowedObjectTypes,
    string Cardinality,
    string? InverseKey,
    string? SensitivityRule,
    /// <summary>Whether a cycle over this predicate is a defect, as for DEPENDS_ON.</summary>
    bool Acyclic = false);

/// <summary>What a proposal would change, and why it could not.</summary>
public sealed record GraphChangeSet(
    IReadOnlyList<KnowledgeEntity> Entities,
    IReadOnlyList<KnowledgeRelation> Relations,
    IReadOnlyList<string> Rejections,
    IReadOnlyList<string> AmbiguousNames);

/// <summary>A bounded view of the graph (RFC 04 rule 2).</summary>
public sealed record Subgraph(
    IReadOnlyList<KnowledgeEntity> Entities,
    IReadOnlyList<KnowledgeRelation> Relations,
    int DepthReached,
    bool Degraded = false,
    string? Degradation = null);

/// <summary>The redirection that makes a merge reversible (RFC 04 rule 4).</summary>
public sealed record MergeRecord(
    string Id, string SurvivorId, string MergedId, string Actor, string AtUtc, bool Reversed);

/// <summary>Where a relation came from (RFC 04 <c>Graph.explain</c>).</summary>
public sealed record RelationProvenance(
    string RelationId, IReadOnlyList<string> SourceMemoryIds, string Status, string AssertedAtUtc);

/// <summary>Raised when a change would break a graph rule.</summary>
public sealed class KnowledgeGraphException : Exception
{
    public KnowledgeGraphException(string message) : base(message)
    {
    }

    public KnowledgeGraphException(string message, IReadOnlyList<string> cycle) : base(message) =>
        Cycle = cycle;

    /// <summary>The offending chain, when the change was rejected for creating a cycle.</summary>
    public IReadOnlyList<string> Cycle { get; } = [];
}
