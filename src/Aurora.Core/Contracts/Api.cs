namespace Aurora.Core.Contracts;

/// <summary>Version of the integration surface (RFC 10 rule 2).</summary>
public static class ApiVersion
{
    public const string Current = "v1";
}

/// <summary>
/// A machine-readable failure (RFC 10).
/// </summary>
/// <remarks>
/// <paramref name="Retryable"/> is part of the contract rather than something a client infers from
/// a status code: whether it is worth trying again is knowledge the server has and the client does
/// not.
/// </remarks>
public sealed record ApiError(
    string Code,
    string Message,
    bool Retryable,
    string? Field = null,
    string? SupportReference = null);

/// <summary>Every response carries the same shell, so a client parses one shape (RFC 10).</summary>
public sealed record ApiEnvelope<T>(
    string RequestId,
    string CorrelationId,
    string ApiVersion,
    T? Data,
    IReadOnlyList<ApiError> Errors,
    IReadOnlyDictionary<string, string> Links);

/// <summary>Stable codes, so a client can branch without matching on prose.</summary>
public static class ApiErrorCode
{
    public const string NotFound = "not_found";
    public const string Invalid = "invalid_request";
    public const string Conflict = "conflict";
    public const string Forbidden = "forbidden";
    public const string IdempotencyKeyRequired = "idempotency_key_required";
    public const string IdempotencyConflict = "idempotency_conflict";
}
