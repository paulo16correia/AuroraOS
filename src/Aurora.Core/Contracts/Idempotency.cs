namespace Aurora.Core.Contracts;

/// <summary>Lifecycle of an idempotent request, keyed by (principal, idempotency_key).</summary>
public static class IdempotencyState
{
    public const string Accepted = "ACCEPTED";
    public const string Executing = "EXECUTING";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
    public const string Unknown = "UNKNOWN";
}

public sealed record IdempotencyEntry(
    string PrincipalClientId,
    string IdempotencyKey,
    string RequestHash,
    string State,
    string? ResultJson,
    string CreatedAtUtc,
    string UpdatedAtUtc);

/// <summary>Outcome of reserving an idempotency key immediately before execution.</summary>
public enum IdempotencyDisposition
{
    /// <summary>Key is newly reserved; the caller proceeds to execute.</summary>
    Begin,

    /// <summary>Key already completed with the same input; the stored response is returned.</summary>
    ReplayCompleted,

    /// <summary>Key already failed with the same input; the stored failure is returned.</summary>
    ReplayFailed,

    /// <summary>Key is currently in flight (Accepted/Executing) for the same input.</summary>
    InProgress,

    /// <summary>Key exists with a different request hash — a genuine conflict.</summary>
    Conflict,

    /// <summary>Key is in an indeterminate state and must be reconciled (It.3).</summary>
    Unknown,
}

/// <summary>
/// Result of <see cref="Abstractions.IIdempotencyStore.BeginAsync"/>. On a replay,
/// <see cref="StoredResultJson"/> holds the serialized <see cref="ExecuteResponse"/> captured
/// on the original run.
/// </summary>
public sealed record IdempotencyBegin(IdempotencyDisposition Disposition, string? StoredResultJson = null);
