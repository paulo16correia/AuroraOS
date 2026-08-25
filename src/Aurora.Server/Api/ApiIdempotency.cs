using System.Text.Json;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Cryptography;
using Aurora.Core;

namespace Aurora.Server.Api;

/// <summary>
/// Makes a write command repeatable (RFC 10 rule 1).
/// </summary>
/// <remarks>
/// Reuses the Kernel's idempotency ledger rather than adding a second one. A repeated request with
/// the same key returns the same logical result; the same key with a different body is a conflict,
/// because silently treating it as a replay would return an answer to a question nobody asked.
/// </remarks>
public static class ApiIdempotency
{
    public static async Task<IResult> RunAsync<T>(
        IIdempotencyStore store,
        Principal principal,
        string? idempotencyKey,
        object request,
        string correlationId,
        Func<CancellationToken, Task<T>> command,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Results.Json(
                ApiEnvelopes.Fail(
                    correlationId, ApiErrorCode.IdempotencyKeyRequired,
                    "A write command must carry an Idempotency-Key header."),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var requestHash = Hashing.Sha256Hex(JsonSerializer.Serialize(request));
        IdempotencyBegin begin = await store
            .BeginAsync(principal, idempotencyKey, requestHash, ct).ConfigureAwait(false);

        switch (begin.Disposition)
        {
            case IdempotencyDisposition.ReplayCompleted:
            case IdempotencyDisposition.ReplayFailed:
                return Results.Content(
                    begin.StoredResultJson ?? "{}", "application/json", statusCode: StatusCodes.Status200OK);

            case IdempotencyDisposition.Conflict:
                return Results.Json(
                    ApiEnvelopes.Fail(
                        correlationId, ApiErrorCode.IdempotencyConflict,
                        "That Idempotency-Key was used with a different request body."),
                    statusCode: StatusCodes.Status409Conflict);

            case IdempotencyDisposition.InProgress:
                return Results.Json(
                    ApiEnvelopes.Fail(
                        correlationId, ApiErrorCode.Conflict,
                        "A request with this Idempotency-Key is already running.", retryable: true),
                    statusCode: StatusCodes.Status409Conflict);
        }

        if (!await store.MarkExecutingAsync(principal, idempotencyKey, ct).ConfigureAwait(false))
        {
            return Results.Json(
                ApiEnvelopes.Fail(correlationId, ApiErrorCode.Conflict, "The reservation was taken.", true),
                statusCode: StatusCodes.Status409Conflict);
        }

        T result;
        try
        {
            result = await command(ct).ConfigureAwait(false);
        }
        catch
        {
            // Release rather than settle: a failed attempt should not replay its failure forever.
            await store.AbandonAsync(principal, idempotencyKey, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        ApiEnvelope<T> envelope = ApiEnvelopes.Ok(result, correlationId);
        var body = AuroraJson.Serialize(envelope);

        await store.CompleteAsync(
            principal, idempotencyKey, IdempotencyState.Completed, body, CancellationToken.None)
            .ConfigureAwait(false);

        return Results.Content(body, "application/json", statusCode: StatusCodes.Status200OK);
    }
}
