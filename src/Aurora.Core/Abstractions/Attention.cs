using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>Decides whether the caller may consider a candidate at all (RFC 023 rule 1).</summary>
public interface IAttentionAuthorization
{
    /// <summary>Called before any score is computed. Authorisation is not a tie-breaker.</summary>
    bool MayConsider(AttentionItem candidate, MemoryAccessContext access);
}

/// <summary>Selects the small set a cycle will process (RFC 023).</summary>
public interface IAttentionSystem
{
    /// <summary>
    /// Ranks candidates into a bounded set, recording why each item was selected or excluded.
    /// Authorisation is applied before relevance, so urgency can never buy access.
    /// </summary>
    Task<AttentionSet> RankAsync(
        string cycleId,
        IReadOnlyList<AttentionItem> candidates,
        AttentionPolicy policy,
        MemoryAccessContext access,
        CancellationToken ct);

    /// <summary>Locks the set so the cycle works against a stable focus.</summary>
    Task<AttentionSet> FocusAsync(string cycleId, string itemRef, CancellationToken ct);

    Task<AttentionSet?> GetAsync(string cycleId, CancellationToken ct);

    Task ReleaseAsync(string cycleId, CancellationToken ct);
}
