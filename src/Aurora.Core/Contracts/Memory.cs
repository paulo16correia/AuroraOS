namespace Aurora.Core.Contracts;

/// <summary>Kinds of memory (RFC 03).</summary>
public static class MemoryKind
{
    public const string Episodic = "EPISODIC";
    public const string Semantic = "SEMANTIC";
    public const string Procedural = "PROCEDURAL";

    /// <summary>Ephemeral. Never enters lasting research.</summary>
    public const string Working = "WORKING";
}

/// <summary>Lifecycle of a memory (RFC 03).</summary>
public static class MemoryStatus
{
    /// <summary>Inferred without confirmation. May guide questions and suggestions, never high-impact actions.</summary>
    public const string Candidate = "CANDIDATE";

    public const string Active = "ACTIVE";
    public const string Disputed = "DISPUTED";
    public const string Superseded = "SUPERSEDED";
    public const string Retracted = "RETRACTED";
    public const string Expired = "EXPIRED";
}

public static class MemoryOrigin
{
    public const string User = "USER";
    public const string System = "SYSTEM";
    public const string Import = "IMPORT";
}

public static class RevisionOperation
{
    public const string Create = "CREATE";
    public const string Confirm = "CONFIRM";
    public const string Correct = "CORRECT";
    public const string Merge = "MERGE";
    public const string Retract = "RETRACT";
    public const string Expire = "EXPIRE";
}

/// <summary>
/// One domain record in Aurora's memory (RFC 03).
/// </summary>
/// <remarks>
/// Named <c>MemoryRecord</c> rather than <c>Memory</c> because <see cref="System.Memory{T}"/> exists
/// in every file that uses <c>System</c>, and an ambiguous domain type is a trap for later readers.
/// <para>
/// Memory is a set of domain records with provenance, not an unlimited archive of conversations.
/// </para>
/// </remarks>
public sealed record MemoryRecord(
    string Id,
    string Kind,
    string SubjectRef,
    string Predicate,
    string ObjectJson,
    string Summary,
    IReadOnlyList<string> SourceRefs,
    IReadOnlyList<string> EvidenceRefs,
    double Confidence,
    string Status,
    string SensitivityClass,
    string AccessPolicyId,
    string? ValidFromUtc,
    string? ValidToUtc,
    string? RetentionUntilUtc,
    string? EmbeddingRef,
    string CreatedBy,
    string ContentHash);

/// <summary>Where a memory came from and who may see it (RFC 03 rule 1).</summary>
public sealed record MemoryProvenance(
    IReadOnlyList<string> SourceRefs,
    IReadOnlyList<string> EvidenceRefs,
    string CreatedBy,
    string AccessPolicyId,
    /// <summary>The specific rule that permits consolidating sensitive material (rule 5).</summary>
    string? SpecificRuleRef = null);

/// <summary>What a caller proposes to remember.</summary>
public sealed record MemoryCandidate(
    string Kind,
    string SubjectRef,
    string Predicate,
    string ObjectJson,
    string Summary,
    double Confidence,
    string SensitivityClass,
    string? ValidFromUtc = null,
    string? ValidToUtc = null,
    string? RetentionUntilUtc = null);

/// <summary>An audited change to a memory (RFC 03).</summary>
public sealed record MemoryRevision(
    string Id,
    string MemoryId,
    string Operation,
    string Actor,
    string Reason,
    string? PriorHash,
    string NewHash,
    string AtUtc);

public sealed record RankedMemory(MemoryRecord Memory, double Score);

/// <summary>
/// The result of a search. Carries whether the answer is trustworthy as an absence.
/// </summary>
/// <remarks>
/// RFC 03 limit case: on index failure the system falls back to a limited structured query and must
/// not declare absence of memory with certainty. <see cref="Confident"/> is how a caller tells
/// "nothing is recorded" apart from "nothing was found by a degraded search".
/// </remarks>
public sealed record MemorySearchResult(
    IReadOnlyList<RankedMemory> Matches, bool Confident, string? Degradation = null);

/// <summary>What the caller may see (RFC 03 rule 2).</summary>
public sealed record MemoryAccessContext(
    string Requester,
    IReadOnlyList<string> AccessPolicyIds,
    string MaxSensitivity);

public sealed record MemoryFilters(
    string? Kind = null, string? SubjectRef = null, bool IncludeCandidates = true);

public sealed class MemoryException : Exception
{
    public MemoryException(string message) : base(message)
    {
    }
}
