using System.Globalization;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Persistence;

/// <summary>
/// SQLite-backed idempotency ledger keyed by (principal client id, idempotency key). Reservation
/// uses an immediate write transaction so concurrent callers serialize; a lost insert race is
/// detected via the primary-key constraint and resolved by re-reading the winning row.
/// </summary>
public sealed class SqliteIdempotencyStore : IIdempotencyStore
{
    /// <summary>SQLite primary result code for a constraint violation (SQLITE_CONSTRAINT).</summary>
    private const int SqliteConstraintError = 19;

    private readonly SqliteConnectionFactory _factory;
    private readonly IClock _clock;

    public SqliteIdempotencyStore(SqliteConnectionFactory factory, IClock clock)
    {
        _factory = factory;
        _clock = clock;
    }

    public async Task<IdempotencyBegin> BeginAsync(Principal principal, string key, string requestHash, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);

            IdempotencyBegin? existing = null;
            await using (var selectCommand = connection.CreateCommand())
            {
                selectCommand.Transaction = transaction;
                selectCommand.CommandText =
                    "SELECT request_hash, state, result_json FROM idempotency " +
                    "WHERE principal_client_id = @c AND idempotency_key = @k;";
                selectCommand.Parameters.AddWithValue("@c", principal.ClientId);
                selectCommand.Parameters.AddWithValue("@k", key);
                await using var reader = await selectCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var storedRequestHash = reader.GetString(0);
                    var storedState = reader.GetString(1);
                    var storedResultJson = reader.IsDBNull(2) ? null : reader.GetString(2);
                    existing = Resolve(storedRequestHash, storedState, storedResultJson, requestHash);
                }
            }

            if (existing is not null)
            {
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return existing;
            }

            try
            {
                var now = _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                await using (var insertCommand = connection.CreateCommand())
                {
                    insertCommand.Transaction = transaction;
                    insertCommand.CommandText = """
                        INSERT INTO idempotency
                            (principal_client_id, idempotency_key, request_hash, state,
                             result_json, created_at_utc, updated_at_utc)
                        VALUES
                            (@c, @k, @rh, @state, NULL, @now, @now);
                        """;
                    insertCommand.Parameters.AddWithValue("@c", principal.ClientId);
                    insertCommand.Parameters.AddWithValue("@k", key);
                    insertCommand.Parameters.AddWithValue("@rh", requestHash);
                    insertCommand.Parameters.AddWithValue("@state", IdempotencyState.Accepted);
                    insertCommand.Parameters.AddWithValue("@now", now);
                    await insertCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return new IdempotencyBegin(IdempotencyDisposition.Begin);
            }
            catch (SqliteException ex) when (attempt == 0 && ex.SqliteErrorCode == SqliteConstraintError)
            {
                // Lost an insert race: another writer created the row first. The uncommitted
                // transaction is rolled back on disposal; loop once more to read the stored row.
            }
        }
    }

    public async Task<bool> MarkExecutingAsync(Principal principal, string key, CancellationToken ct)
    {
        var now = _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE idempotency
            SET state = @state, updated_at_utc = @now
            WHERE principal_client_id = @c AND idempotency_key = @k AND state = @from;
            """;
        command.Parameters.AddWithValue("@state", IdempotencyState.Executing);
        command.Parameters.AddWithValue("@now", now);
        command.Parameters.AddWithValue("@c", principal.ClientId);
        command.Parameters.AddWithValue("@k", key);
        command.Parameters.AddWithValue("@from", IdempotencyState.Accepted);

        // Compare-and-set: exactly one row transitions ACCEPTED→EXECUTING. Zero means the caller
        // does not own the reservation (state changed/removed) and must not proceed.
        var affected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return affected == 1;
    }

    public async Task CompleteAsync(Principal principal, string key, string state, string resultJson, CancellationToken ct)
    {
        var now = _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE idempotency
            SET state = @state, result_json = @result, updated_at_utc = @now
            WHERE principal_client_id = @c AND idempotency_key = @k;
            """;
        command.Parameters.AddWithValue("@state", state);
        command.Parameters.AddWithValue("@result", resultJson);
        command.Parameters.AddWithValue("@now", now);
        command.Parameters.AddWithValue("@c", principal.ClientId);
        command.Parameters.AddWithValue("@k", key);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static IdempotencyBegin Resolve(string storedRequestHash, string storedState, string? storedResultJson, string requestHash)
    {
        if (!string.Equals(storedRequestHash, requestHash, StringComparison.Ordinal))
        {
            return new IdempotencyBegin(IdempotencyDisposition.Conflict);
        }

        return storedState switch
        {
            IdempotencyState.Completed => new IdempotencyBegin(IdempotencyDisposition.ReplayCompleted, storedResultJson),
            IdempotencyState.Failed => new IdempotencyBegin(IdempotencyDisposition.ReplayFailed, storedResultJson),
            IdempotencyState.Accepted or IdempotencyState.Executing => new IdempotencyBegin(IdempotencyDisposition.InProgress),
            _ => new IdempotencyBegin(IdempotencyDisposition.Unknown),
        };
    }
}
