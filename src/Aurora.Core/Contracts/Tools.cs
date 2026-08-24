namespace Aurora.Core.Contracts;

public static class ToolCallStatus
{
    public const string Proposed = "PROPOSED";
    public const string Authorized = "AUTHORIZED";

    /// <summary>Deferred by a rate limit, with a retry_after. Never a tight repetition.</summary>
    public const string Queued = "QUEUED";

    public const string Running = "RUNNING";
    public const string Succeeded = "SUCCEEDED";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";

    /// <summary>
    /// We did not receive a response. RFC 06 is explicit that this does not mean it did not happen.
    /// </summary>
    public const string Unknown = "UNKNOWN";
}

public static class AuthMode
{
    public const string None = "NONE";
    public const string VaultSecret = "VAULT_SECRET";
}

/// <summary>The reduced contract a connector offers (RFC 06).</summary>
public sealed record ToolManifest(
    string ToolId,
    string Version,
    string Provider,
    IReadOnlyList<string> Capabilities,
    string InputSchema,
    string OutputSchema,
    IReadOnlyList<string> Effects,
    IReadOnlyList<string> DataClassesIn,
    IReadOnlyList<string> DataClassesOut,
    string AuthMode,
    int TimeoutSeconds,
    int RateLimitPerMinute,
    bool RequiresApproval,
    string? SecretReferenceId = null)
{
    /// <summary>A tool that changes something outside Aurora.</summary>
    public bool IsWriting => Effects.Count > 0;
}

public sealed record ToolCall(
    string Id,
    string WorkItemId,
    string? TaskId,
    string ToolId,
    string Capability,
    string InputRedactedJson,
    string InputHash,
    string? IdempotencyKey,
    string Status,
    IReadOnlyList<string> PolicyDecisionIds,
    string? ApprovalId,
    string? StartedAtUtc,
    string? EndedAtUtc,
    string? ExternalReference,
    string? OutputRef,
    string? ErrorCode,
    string? RetryAfterUtc = null);

public sealed record ToolArtifact(string Kind, string Ref);

/// <summary>What a connector returns. Untrusted until it has passed the output schema.</summary>
public sealed record ToolResult(
    string Status,
    string? StructuredOutputJson,
    IReadOnlyList<ToolArtifact> Artifacts,
    IReadOnlyList<string> EvidenceRefs,
    bool Retryable,
    string UserSafeSummary,
    string? ExternalReference = null,
    string? ErrorCode = null);

public sealed class ToolException : Exception
{
    public ToolException(string message) : base(message)
    {
    }
}
