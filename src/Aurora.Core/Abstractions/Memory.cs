using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>What a forget request actually did, so the caller can be told the real scope.</summary>
public sealed record MemoryTombstone(
    string MemoryId, MemoryRevision Revision, bool RemovedFromActiveIndexes, string Scope);

/// <summary>
/// Ranks memories that already passed the access filter.
/// </summary>
/// <remarks>
/// Separate from the store on purpose. RFC 03 rule 2 requires ACL and classification to be applied
/// <b>before</b> semantic calculation, and keeping ranking behind its own interface makes that
/// ordering structural rather than a comment — the ranker is only ever handed records the caller is
/// already allowed to see.
/// </remarks>
public interface IMemoryRanker
{
    IReadOnlyList<RankedMemory> Rank(string query, IReadOnlyList<MemoryRecord> permitted);
}

/// <summary>Useful, correctable memory with provenance (RFC 03).</summary>
public interface IMemoryService
{
    /// <summary>
    /// Records a candidate. Refuses without an origin and an access policy, and refuses to
    /// consolidate sensitive material without the specific rule that permits it.
    /// </summary>
    Task<MemoryRecord> RecordAsync(
        MemoryCandidate candidate, MemoryProvenance provenance, CancellationToken ct);

    /// <summary>Applies access and classification first, then ranks what remains.</summary>
    Task<MemorySearchResult> SearchAsync(
        string query, MemoryAccessContext access, MemoryFilters filters, CancellationToken ct);

    /// <summary>Applies an audited revision. An owner's correction outranks automatic inference.</summary>
    Task<MemoryRevision> ReviseAsync(
        string memoryId, string operation, string actor, string reason, CancellationToken ct);

    /// <summary>Retracts a memory and reports what that actually removed.</summary>
    Task<MemoryTombstone> ForgetAsync(string memoryId, string actor, CancellationToken ct);

    Task<MemoryRecord?> GetAsync(string memoryId, CancellationToken ct);

    Task<IReadOnlyList<MemoryRevision>> RevisionsAsync(string memoryId, CancellationToken ct);
}
