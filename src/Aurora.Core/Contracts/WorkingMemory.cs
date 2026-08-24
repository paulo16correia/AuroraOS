namespace Aurora.Core.Contracts;

public static class WorkingMemoryStatus
{
    public const string Open = "OPEN";
    public const string Sealed = "SEALED";
    public const string Expired = "EXPIRED";
    public const string Discarded = "DISCARDED";
}

public static class WorkingItemType
{
    public const string Context = "CONTEXT";
    public const string Draft = "DRAFT";

    /// <summary>A guess. It stays marked as one and never becomes a fact by itself.</summary>
    public const string Hypothesis = "HYPOTHESIS";

    public const string Result = "RESULT";
    public const string Question = "QUESTION";
}

public static class WorkingItemDisposition
{
    public const string Pending = "PENDING";
    public const string Discard = "DISCARD";
    public const string Audit = "AUDIT";
    public const string Consolidate = "CONSOLIDATE";
}

/// <summary>The isolated, bounded space one cycle thinks in (RFC 024).</summary>
public sealed record WorkingMemoryFrame(
    string Id,
    string CycleId,
    string? SessionId,
    string Status,
    int CapacityTokens,
    int CapacityItems,
    string SensitivityCeiling,
    string ExpiresAtUtc,
    int UsedTokens,
    int UsedItems);

public sealed record WorkingItem(
    string Id,
    string WorkingMemoryId,
    string Type,
    string? PayloadJson,
    string? PayloadRef,
    IReadOnlyList<string> SourceRefs,
    double Confidence,
    string SensitivityClass,
    int TokenCost,
    string CreatedAtUtc,
    string? ExpiresAtUtc,
    string Disposition);

/// <summary>
/// A proposal to keep something beyond the cycle.
/// </summary>
/// <remarks>
/// <see cref="MustEnterAsCandidate"/> is true for a hypothesis. RFC 024 rule 3 forbids a hypothesis
/// becoming a fact without the RFC 03 flow, so consolidation can only ever offer it as a candidate
/// for confirmation — never as an active memory.
/// </remarks>
public sealed record ConsolidationProposal(
    string WorkingItemId, string Type, string Summary, bool MustEnterAsCandidate);

/// <summary>What disposing a frame actually did (RFC 024).</summary>
public sealed record DisposalReport(
    string WorkingMemoryId,
    int Discarded,
    int SentToAudit,
    IReadOnlyList<ConsolidationProposal> Consolidations,
    /// <summary>An approved operational summary — never raw drafts presented as "reasoning".</summary>
    string Summary);

public sealed class WorkingMemoryException : Exception
{
    public WorkingMemoryException(string message) : base(message)
    {
    }
}

/// <summary>Raised when capacity is exhausted and truncating would drop sensitive content.</summary>
public sealed class WorkingMemoryFullException : Exception
{
    public WorkingMemoryFullException(string message) : base(message)
    {
    }
}
