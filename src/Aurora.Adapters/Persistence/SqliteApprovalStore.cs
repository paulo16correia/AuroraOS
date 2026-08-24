using System.Globalization;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Persistence;

/// <summary>
/// SQLite-backed approval ledger. A live PENDING row is unique per (principal, action_id,
/// scope_hash) via a partial unique index; a lost insert race is detected via the constraint and
/// resolved by re-reading the winning row, the same pattern <see cref="SqliteIdempotencyStore"/> uses.
/// </summary>
public sealed class SqliteApprovalStore : IApprovalStore
{
    /// <summary>SQLite primary result code for a constraint violation (SQLITE_CONSTRAINT).</summary>
    private const int SqliteConstraintError = 19;

    /// <summary>Single window covering request → decide → consume (docs/adr/0002).</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private readonly SqliteConnectionFactory _factory;
    private readonly IClock _clock;

    public SqliteApprovalStore(SqliteConnectionFactory factory, IClock clock)
    {
        _factory = factory;
        _clock = clock;
    }

    public async Task<ApprovalEvaluation> EvaluateAsync(
        Principal principal, string actionId, string scopeHash, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);

            var nowText = _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            string? liveApprovedId = null;
            string? rejectedId = null;
            string? livePendingId = null;

            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = """
                    SELECT approval_id, status, expires_at_utc FROM approval
                    WHERE principal_client_id = @c AND action_id = @a AND scope_hash = @s
                    ORDER BY created_at_utc DESC;
                    """;
                select.Parameters.AddWithValue("@c", principal.ClientId);
                select.Parameters.AddWithValue("@a", actionId);
                select.Parameters.AddWithValue("@s", scopeHash);
                await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var id = reader.GetString(0);
                    var status = reader.GetString(1);
                    var live = string.CompareOrdinal(reader.GetString(2), nowText) > 0;

                    if (status == ApprovalStatus.Approved && live)
                    {
                        liveApprovedId ??= id;
                    }
                    else if (status == ApprovalStatus.Rejected)
                    {
                        rejectedId ??= id;
                    }
                    else if (status == ApprovalStatus.Pending && live)
                    {
                        livePendingId ??= id;
                    }
                }
            }

            if (liveApprovedId is not null)
            {
                await using var consume = connection.CreateCommand();
                consume.Transaction = transaction;
                consume.CommandText = """
                    UPDATE approval SET status = @consumed
                    WHERE approval_id = @id AND status = @approved;
                    """;
                consume.Parameters.AddWithValue("@consumed", ApprovalStatus.Consumed);
                consume.Parameters.AddWithValue("@id", liveApprovedId);
                consume.Parameters.AddWithValue("@approved", ApprovalStatus.Approved);
                var affected = await consume.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (affected == 1)
                {
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                    return new ApprovalEvaluation(ApprovalOutcome.Consumed, liveApprovedId);
                }

                // Lost a race to consume the same approval; re-evaluate as if it were already gone.
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                continue;
            }

            if (rejectedId is not null)
            {
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return new ApprovalEvaluation(ApprovalOutcome.Rejected, rejectedId);
            }

            if (livePendingId is not null)
            {
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return new ApprovalEvaluation(ApprovalOutcome.Pending, livePendingId);
            }

            try
            {
                var newId = Guid.NewGuid().ToString("N");
                var now = _clock.UtcNow;
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO approval
                        (approval_id, principal_client_id, principal_os_user, action_id, scope_hash,
                         status, created_at_utc, expires_at_utc, decided_at_utc)
                    VALUES
                        (@id, @c, @wu, @a, @s, @status, @now, @exp, NULL);
                    """;
                insert.Parameters.AddWithValue("@id", newId);
                insert.Parameters.AddWithValue("@c", principal.ClientId);
                insert.Parameters.AddWithValue("@wu", principal.OsUser);
                insert.Parameters.AddWithValue("@a", actionId);
                insert.Parameters.AddWithValue("@s", scopeHash);
                insert.Parameters.AddWithValue("@status", ApprovalStatus.Pending);
                insert.Parameters.AddWithValue("@now", now.ToString("O", CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("@exp", now.Add(Ttl).ToString("O", CultureInfo.InvariantCulture));
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return new ApprovalEvaluation(ApprovalOutcome.Pending, newId);
            }
            catch (SqliteException ex) when (attempt == 0 && ex.SqliteErrorCode == SqliteConstraintError)
            {
                // Lost the race against a concurrent PENDING insert for the same live scope (the
                // partial unique index enforces at most one). Loop once more to read the winner.
            }
        }
    }

    public async Task<int> CountPendingAsync(CancellationToken ct)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // Expired rows are excluded: they are pending in the table but nobody is waiting on them,
        // and counting them would make the gauge climb forever.
        command.CommandText = """
            SELECT COUNT(*) FROM approval WHERE status = @pending AND expires_at_utc > @now;
            """;
        command.Parameters.AddWithValue("@pending", ApprovalStatus.Pending);
        command.Parameters.AddWithValue("@now", _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task<ApprovalDecideResult> DecideAsync(
        Principal principal, string approvalId, bool approve, CancellationToken ct)
    {
        var nowText = _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var newStatus = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;

        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);

        int affected;
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            // Compare-and-set: only a live (unexpired) PENDING row owned by this principal decides.
            update.CommandText = """
                UPDATE approval
                SET status = @newStatus, decided_at_utc = @now
                WHERE approval_id = @id AND principal_client_id = @c
                      AND status = @pending AND expires_at_utc > @now;
                """;
            update.Parameters.AddWithValue("@newStatus", newStatus);
            update.Parameters.AddWithValue("@now", nowText);
            update.Parameters.AddWithValue("@id", approvalId);
            update.Parameters.AddWithValue("@c", principal.ClientId);
            update.Parameters.AddWithValue("@pending", ApprovalStatus.Pending);
            affected = await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (affected != 1)
        {
            // Distinguish "unknown / belongs to someone else" from "exists but not decidable".
            await using var probe = connection.CreateCommand();
            probe.Transaction = transaction;
            probe.CommandText = "SELECT 1 FROM approval WHERE approval_id = @id AND principal_client_id = @c;";
            probe.Parameters.AddWithValue("@id", approvalId);
            probe.Parameters.AddWithValue("@c", principal.ClientId);
            var exists = await probe.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new ApprovalDecideResult(
                exists ? ApprovalDecideOutcome.NotPending : ApprovalDecideOutcome.NotFound, null);
        }

        ApprovalRecord? record = null;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT approval_id, principal_client_id, principal_os_user, action_id, scope_hash,
                       status, created_at_utc, expires_at_utc, decided_at_utc
                FROM approval WHERE approval_id = @id;
                """;
            select.Parameters.AddWithValue("@id", approvalId);
            await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                record = new ApprovalRecord(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8));
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new ApprovalDecideResult(ApprovalDecideOutcome.Decided, record);
    }
}
