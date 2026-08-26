namespace Aurora.Core.Contracts;

/// <summary>Where a unit of work has got to (RFC 02).</summary>
public static class WorkItemStatus
{
    public const string Received = "RECEIVED";
    public const string Contextualized = "CONTEXTUALIZED";
    public const string Deliberating = "DELIBERATING";
    public const string WaitingApproval = "WAITING_APPROVAL";
    public const string Executing = "EXECUTING";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";

    /// <summary>Whether work is still going on, which is what rule 1 counts.</summary>
    public static bool IsActive(string status) =>
        status is Received or Contextualized or Deliberating or WaitingApproval or Executing;
}

/// <summary>
/// One unit of work, from the thing that arrived to the thing that came of it (RFC 02).
/// </summary>
/// <remarks>
/// Until this existed, <c>CognitiveCycle.WorkItemId</c> and <c>tool_call.work_item_id</c> both
/// referenced an object with no type, no table and no lifecycle. What they actually held was a
/// subject reference — <c>mcp/echo.say</c> for every call of the same capability — so "the cycles
/// of this work item" was not a question the column could answer.
/// </remarks>
public sealed record WorkItem(
    string Id,
    string CorrelationId,
    /// <summary>What caused this, when something else did (RFC 050's causation chain).</summary>
    string? CausationId,
    string? EventId,
    string Status,
    string? DeadlineAtUtc,
    int RetryCount,
    /// <summary>
    /// Rule 1: at most one active work item per key. The same request arriving twice joins the
    /// work already in flight rather than starting a second one beside it.
    /// </summary>
    string IdempotencyKey,
    string CreatedAtUtc,
    string UpdatedAtUtc,
    string? CancelledBy = null);

public sealed class WorkItemException : Exception
{
    public WorkItemException(string message) : base(message)
    {
    }
}
