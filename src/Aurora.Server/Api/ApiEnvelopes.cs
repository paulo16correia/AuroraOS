using Aurora.Core.Contracts;

namespace Aurora.Server.Api;

/// <summary>Builds the RFC 10 envelope, so no endpoint assembles one by hand.</summary>
public static class ApiEnvelopes
{
    public static ApiEnvelope<T> Ok<T>(
        T data, string correlationId, IReadOnlyDictionary<string, string>? links = null) =>
        new(Guid.NewGuid().ToString("N"), correlationId, ApiVersion.Current, data, [],
            links ?? new Dictionary<string, string>(StringComparer.Ordinal));

    public static ApiEnvelope<object> Fail(
        string correlationId, string code, string message, bool retryable = false, string? field = null) =>
        new(Guid.NewGuid().ToString("N"), correlationId, ApiVersion.Current, null,
            [new ApiError(code, message, retryable, field)],
            new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>The correlation id a client supplied, or a fresh one so every response has one.</summary>
    public static string CorrelationOf(HttpRequest request) =>
        request.Headers.TryGetValue("Correlation-Id", out var supplied) && !string.IsNullOrWhiteSpace(supplied)
            ? supplied.ToString()
            : Guid.NewGuid().ToString("N");
}
