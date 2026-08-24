using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>How the caller wants each item disposed of when a frame closes.</summary>
public sealed record ConsolidationDecision(string WorkingItemId, string Disposition);

/// <summary>The temporary, isolated space one cycle works in (RFC 024).</summary>
public interface IWorkingMemory
{
    /// <summary>Opens a frame for a cycle, seeded from its attention set.</summary>
    Task<WorkingMemoryFrame> OpenAsync(
        string cycleId, string? sessionId, AttentionSet attention, AttentionPolicy policy, CancellationToken ct);

    /// <summary>
    /// Adds an item. Refuses when capacity is exhausted and dropping something would silently lose
    /// sensitive content, and refuses anything above the frame's ceiling.
    /// </summary>
    Task<WorkingItem> PutAsync(string workingMemoryId, WorkingItem item, CancellationToken ct);

    /// <summary>Seals the frame; nothing more may be added.</summary>
    Task<WorkingMemoryFrame> SealAsync(string workingMemoryId, CancellationToken ct);

    /// <summary>Applies dispositions and reports what was kept, audited or proposed.</summary>
    Task<DisposalReport> DisposeFrameAsync(
        string workingMemoryId, IReadOnlyList<ConsolidationDecision> decisions, CancellationToken ct);

    /// <summary>Seals and expires frames past their TTL (RFC 024 rule 2).</summary>
    Task<int> ExpireDueAsync(CancellationToken ct);

    Task<WorkingMemoryFrame?> GetAsync(string workingMemoryId, CancellationToken ct);

    Task<IReadOnlyList<WorkingItem>> ItemsAsync(string workingMemoryId, CancellationToken ct);

    /// <summary>Moves an item between frames. RFC 024 rule 1: sharing is explicit, never implicit.</summary>
    Task<WorkingItem> TransferAsync(
        string itemId, string toWorkingMemoryId, string reason, CancellationToken ct);
}
