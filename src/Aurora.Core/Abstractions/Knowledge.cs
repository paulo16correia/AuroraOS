using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>What to walk from, and how far (RFC 04 rule 2).</summary>
public sealed record GraphPattern(
    string? StartEntityId = null,
    string? SearchName = null,
    string? EntityType = null,
    IReadOnlyList<string>? Predicates = null,
    bool AsOfNowOnly = true);

/// <summary>Entities, relations and their provenance (RFC 04).</summary>
public interface IKnowledgeGraph
{
    Task<PredicateSchema> RegisterPredicateAsync(PredicateSchema schema, CancellationToken ct);

    Task<KnowledgeEntity> UpsertEntityAsync(KnowledgeEntity entity, CancellationToken ct);

    /// <summary>
    /// Turns one memory into a typed node/edge proposal. Rejects anything the schema does not
    /// allow rather than inventing a predicate.
    /// </summary>
    Task<GraphChangeSet> ProposeAsync(string memoryId, CancellationToken ct);

    /// <summary>
    /// Asserts an edge between two entities, as RFC 04's own examples do
    /// (<c>DEPENDS_ON(Task, Task)</c>). Rejects an unregistered predicate, a type the schema
    /// does not allow, and a change that would close a cycle over an acyclic predicate.
    /// </summary>
    Task<KnowledgeRelation> AssertRelationAsync(
        string subjectId, string predicate, string objectId,
        IReadOnlyList<string> sourceMemoryIds, CancellationToken ct);

    /// <summary>Walks the graph, clamped to a maximum depth and to what the caller may see.</summary>
    Task<Subgraph> QueryAsync(
        GraphPattern pattern, int depth, MemoryAccessContext access, CancellationToken ct);

    /// <summary>Merges two entities, leaving a redirection so the merge can be undone.</summary>
    Task<MergeRecord> MergeAsync(string survivorId, string mergedId, string actor, CancellationToken ct);

    /// <summary>Reverses a merge using its record (RFC 04 rule 4).</summary>
    Task<MergeRecord> UnmergeAsync(string mergeRecordId, string actor, CancellationToken ct);

    Task<IReadOnlyList<RelationProvenance>> ExplainAsync(string relationId, CancellationToken ct);

    /// <summary>
    /// Handles a withdrawn source: the edge is preserved for audit but loses ASSERTED, because a
    /// fact whose evidence is gone is no longer a fact.
    /// </summary>
    Task<int> OnSourceWithdrawnAsync(string memoryId, CancellationToken ct);

    Task<KnowledgeEntity?> GetEntityAsync(string entityId, CancellationToken ct);
}
