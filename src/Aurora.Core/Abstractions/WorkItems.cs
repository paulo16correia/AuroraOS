using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// Units of work, one per thing that arrived (RFC 02).
/// </summary>
/// <remarks>
/// RFC 02 names <c>Kernel.handle(event_id) -> WorkItem</c> and <c>Kernel.cancel(work_item_id,
/// actor)</c>. The cycle that RFC 021 governs runs inside one of these; the work item is what the
/// cycle belongs to, and what a second identical request joins instead of duplicating.
/// </remarks>
public interface IWorkItemService
{
    /// <summary>
    /// Opens a work item, or returns the active one for the same idempotency key (rule 1).
    /// </summary>
    Task<WorkItem> HandleAsync(
        string correlationId, string idempotencyKey, string? causationId, string? eventId,
        string? deadlineAtUtc, CancellationToken ct);

    /// <summary>Moves it along. A terminal status is terminal.</summary>
    Task<WorkItem> AdvanceAsync(string workItemId, string status, CancellationToken ct);

    /// <summary>Cancels it, naming who did.</summary>
    Task<WorkItem> CancelAsync(string workItemId, string actor, CancellationToken ct);

    Task<WorkItem?> GetAsync(string workItemId, CancellationToken ct);

    /// <summary>Everything still in flight, oldest first.</summary>
    Task<IReadOnlyList<WorkItem>> ActiveAsync(CancellationToken ct);
}
