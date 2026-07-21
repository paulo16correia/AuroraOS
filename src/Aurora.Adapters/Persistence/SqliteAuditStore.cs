using System.Collections.Concurrent;
using System.Globalization;
using Aurora.Core.Abstractions;
using Aurora.Core.Cryptography;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Persistence;

/// <summary>
/// SQLite-backed, append-only, hash-chained audit log. Appends are serialized process-wide per
/// database (via a per-<see cref="SqliteConnectionFactory.DbPath"/> semaphore) so the chain stays
/// strictly linear. Verification recomputes every record hash and every link, fail-closed.
/// </summary>
/// <remarks>
/// The chain is unkeyed: an in-place edit of any record is tamper-evident, but truncation of the
/// newest rows or a fully consistent rewrite by a party with write access to the database file is
/// NOT detected. External head anchoring / keyed (HMAC) signing is deferred to It.3.
/// </remarks>
public sealed class SqliteAuditStore : IAuditStore
{
    /// <summary>ASCII Unit Separator (U+001F) delimiting the hash pre-image fields.</summary>
    private const char UnitSeparator = (char)0x1F;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    private readonly SqliteConnectionFactory _factory;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate;

    public SqliteAuditStore(SqliteConnectionFactory factory, IClock clock)
    {
        _factory = factory;
        _clock = clock;
        _gate = Gates.GetOrAdd(factory.DbPath, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task<string> AppendAsync(
        string principalClientId,
        string principalWindowsUser,
        string actionId,
        string inputHash,
        string outcome,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);

            long nextSequence;
            string previousHash;
            await using (var readCommand = connection.CreateCommand())
            {
                readCommand.Transaction = transaction;
                readCommand.CommandText =
                    "SELECT sequence, record_hash FROM audit_record ORDER BY sequence DESC LIMIT 1;";
                await using var reader = await readCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    nextSequence = reader.GetInt64(0) + 1;
                    previousHash = reader.GetString(1);
                }
                else
                {
                    nextSequence = 1;
                    previousHash = string.Empty;
                }
            }

            var recordId = Guid.NewGuid().ToString("N");
            var createdAt = _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var recordHash = ComputeRecordHash(
                previousHash,
                nextSequence,
                recordId,
                principalClientId,
                principalWindowsUser,
                actionId,
                inputHash,
                outcome,
                createdAt);

            await using (var insertCommand = connection.CreateCommand())
            {
                insertCommand.Transaction = transaction;
                insertCommand.CommandText = """
                    INSERT INTO audit_record
                        (sequence, record_id, principal_client_id, principal_windows_user,
                         action_id, input_hash, outcome, created_at_utc, previous_hash, record_hash)
                    VALUES
                        (@seq, @rid, @cid, @wu, @aid, @ih, @out, @ts, @prev, @rh);
                    """;
                insertCommand.Parameters.AddWithValue("@seq", nextSequence);
                insertCommand.Parameters.AddWithValue("@rid", recordId);
                insertCommand.Parameters.AddWithValue("@cid", principalClientId);
                insertCommand.Parameters.AddWithValue("@wu", principalWindowsUser);
                insertCommand.Parameters.AddWithValue("@aid", actionId);
                insertCommand.Parameters.AddWithValue("@ih", inputHash);
                insertCommand.Parameters.AddWithValue("@out", outcome);
                insertCommand.Parameters.AddWithValue("@ts", createdAt);
                insertCommand.Parameters.AddWithValue("@prev", previousHash);
                insertCommand.Parameters.AddWithValue("@rh", recordHash);
                await insertCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return recordHash;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AuditVerification> VerifyChainAsync(CancellationToken ct)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, record_id, principal_client_id, principal_windows_user,
                   action_id, input_hash, outcome, created_at_utc, previous_hash, record_hash
            FROM audit_record
            ORDER BY sequence ASC;
            """;

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var isFirst = true;
        var expectedPreviousHash = string.Empty;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var sequence = reader.GetInt64(0);
            var recordId = reader.GetString(1);
            var principalClientId = reader.GetString(2);
            var principalWindowsUser = reader.GetString(3);
            var actionId = reader.GetString(4);
            var inputHash = reader.GetString(5);
            var outcome = reader.GetString(6);
            var createdAt = reader.GetString(7);
            var previousHash = reader.GetString(8);
            var recordHash = reader.GetString(9);

            // (a) first row must chain from the empty hash; (b) every later row from its predecessor.
            var linkOk = isFirst
                ? previousHash.Length == 0
                : string.Equals(previousHash, expectedPreviousHash, StringComparison.Ordinal);
            if (!linkOk)
            {
                return new AuditVerification(false, sequence);
            }

            // (c) the stored hash must match a recomputation over the stored fields.
            var expectedHash = ComputeRecordHash(
                previousHash,
                sequence,
                recordId,
                principalClientId,
                principalWindowsUser,
                actionId,
                inputHash,
                outcome,
                createdAt);
            if (!string.Equals(expectedHash, recordHash, StringComparison.Ordinal))
            {
                return new AuditVerification(false, sequence);
            }

            expectedPreviousHash = recordHash;
            isFirst = false;
        }

        return new AuditVerification(true, null);
    }

    private static string ComputeRecordHash(
        string previousHash,
        long sequence,
        string recordId,
        string principalClientId,
        string principalWindowsUser,
        string actionId,
        string inputHash,
        string outcome,
        string createdAt)
    {
        var preimage = string.Join(
            UnitSeparator,
            new[]
            {
                previousHash,
                sequence.ToString(CultureInfo.InvariantCulture),
                recordId,
                principalClientId,
                principalWindowsUser,
                actionId,
                inputHash,
                outcome,
                createdAt,
            });
        return Hashing.Sha256Hex(preimage);
    }
}
