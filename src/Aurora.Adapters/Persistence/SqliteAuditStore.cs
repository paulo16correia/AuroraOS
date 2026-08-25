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
/// The chain is keyed (HMAC-SHA-256) with a secret held outside the database, and the head is
/// mirrored to an external anchor (docs/adr/0005). Together these cover the three tampering
/// shapes: an in-place edit breaks the recomputation, a wholesale rewrite cannot be signed
/// without the key, and a truncated tail is caught by the anchor being ahead of the database.
/// </remarks>
public sealed class SqliteAuditStore : IAuditStore
{
    /// <summary>ASCII Unit Separator (U+001F) delimiting the hash pre-image fields.</summary>
    private const char UnitSeparator = (char)0x1F;

    /// <summary>Pre-image layout tag, signed along with the fields it describes.</summary>
    private const string PreimageVersion = "v2";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    private readonly SqliteConnectionFactory _factory;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate;
    private readonly byte[] _key;
    private readonly AuditAnchorFile _anchor;

    public SqliteAuditStore(
        SqliteConnectionFactory factory, IClock clock, byte[] key, AuditAnchorFile anchor)
    {
        _factory = factory;
        _clock = clock;
        _key = key;
        _anchor = anchor;
        _gate = Gates.GetOrAdd(factory.DbPath, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task<string> AppendAsync(AuditEntry entry, CancellationToken ct)
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
            var recordHash = ComputeRecordHash(previousHash, nextSequence, recordId, entry, createdAt);

            await using (var insertCommand = connection.CreateCommand())
            {
                insertCommand.Transaction = transaction;
                insertCommand.CommandText = """
                    INSERT INTO audit_record
                        (sequence, record_id, principal_client_id, principal_os_user,
                         action_id, input_hash, outcome, created_at_utc, previous_hash, record_hash,
                         risk, via, decision, policy_ids, reason)
                    VALUES
                        (@seq, @rid, @cid, @wu, @aid, @ih, @out, @ts, @prev, @rh,
                         @risk, @via, @dec, @pol, @reason);
                    """;
                insertCommand.Parameters.AddWithValue("@seq", nextSequence);
                insertCommand.Parameters.AddWithValue("@rid", recordId);
                insertCommand.Parameters.AddWithValue("@cid", entry.PrincipalClientId);
                insertCommand.Parameters.AddWithValue("@wu", entry.PrincipalOsUser);
                insertCommand.Parameters.AddWithValue("@aid", entry.ActionId);
                insertCommand.Parameters.AddWithValue("@ih", entry.InputHash);
                insertCommand.Parameters.AddWithValue("@out", entry.Outcome);
                insertCommand.Parameters.AddWithValue("@ts", createdAt);
                insertCommand.Parameters.AddWithValue("@prev", previousHash);
                insertCommand.Parameters.AddWithValue("@rh", recordHash);
                insertCommand.Parameters.AddWithValue("@risk", (object?)entry.Risk ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue("@via", (object?)entry.Via ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue("@dec", (object?)entry.Decision ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue("@pol", (object?)entry.PolicyIds ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue("@reason", (object?)entry.Reason ?? DBNull.Value);
                await insertCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);

            // Anchor only after the commit: an anchor ahead of a rolled-back append would look
            // exactly like truncation and raise a false alarm.
            _anchor.Advance(nextSequence, recordHash);
            return recordHash;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> HeadHashAsync(CancellationToken ct)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT record_hash FROM audit_record ORDER BY sequence DESC LIMIT 1;";
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    public async Task<IReadOnlyList<AuditRecordView>> QueryAsync(
        long afterSequence, int limit, CancellationToken ct)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, record_id, principal_client_id, action_id, outcome, created_at_utc,
                   record_hash, risk, via, decision, policy_ids, reason
              FROM audit_record WHERE sequence > @after ORDER BY sequence ASC LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@after", afterSequence);
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));

        var rows = new List<AuditRecordView>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new AuditRecordView(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        return rows;
    }

    /// <summary>
    /// The action id that marks a deliberate, acknowledged discontinuity in the chain.
    /// </summary>
    /// <remarks>
    /// Not a capability. It appears in <c>action_id</c> because that is the field verification
    /// already reads, and because the break should be as visible as any other line in the log.
    /// </remarks>
    public const string ChainBreakAction = "audit.chain_break";

    public async Task<string> SealBreakAsync(string reason, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            // Nobody should be able to find an unexplained break in an audit log. Least of all
            // one Aurora put there itself.
            throw new InvalidOperationException("Sealing a break in the audit chain requires a reason.");
        }

        // Appended like any other record, so it is signed with the current key and chained from
        // the current head. Verification recognises it and restarts from here; someone who can
        // write to the database but does not hold the key cannot forge one.
        return await AppendAsync(
            new AuditEntry(
                PrincipalClientId: actor,
                PrincipalOsUser: actor,
                ActionId: ChainBreakAction,
                InputHash: Hashing.Sha256Hex(reason),
                Outcome: "chain_break_acknowledged",
                Risk: "High",
                Via: "operator",
                Decision: "acknowledged",
                PolicyIds: null,
                Reason: reason),
            ct).ConfigureAwait(false);
    }

    public async Task<AuditVerification> VerifyChainAsync(CancellationToken ct)
    {
        IReadOnlyList<StoredRecord> records = await ReadChainAsync(ct).ConfigureAwait(false);

        // A seal excuses everything before it, so it has to be found before the walk starts —
        // otherwise verification stops at the first record the missing key can no longer explain
        // and never reaches the marker that says why. A seal only counts when its own signature
        // recomputes under the current key: someone with write access but no key cannot plant one.
        var seam = -1;
        for (var i = records.Count - 1; i >= 0; i--)
        {
            StoredRecord candidate = records[i];
            if (candidate.Entry.ActionId != ChainBreakAction)
            {
                continue;
            }

            var expected = ComputeRecordHash(
                candidate.PreviousHash, candidate.Sequence, candidate.RecordId,
                candidate.Entry, candidate.CreatedAt);

            if (string.Equals(expected, candidate.RecordHash, StringComparison.Ordinal))
            {
                seam = i;
                break;
            }
        }

        long? breakAt = seam >= 0 ? records[seam].Sequence : null;
        var expectedPreviousHash = string.Empty;
        long headSequence = 0;
        var headHash = string.Empty;
        var isFirst = true;

        for (var i = Math.Max(seam, 0); i < records.Count; i++)
        {
            StoredRecord record = records[i];

            // (c) the stored hash must match a recomputation over the stored fields.
            var expectedHash = ComputeRecordHash(
                record.PreviousHash, record.Sequence, record.RecordId, record.Entry, record.CreatedAt);

            if (!string.Equals(expectedHash, record.RecordHash, StringComparison.Ordinal))
            {
                return new AuditVerification(false, record.Sequence, null, breakAt);
            }

            // (a) the first row chains from the empty hash, or from nothing at all when it is the
            //     seal itself; (b) every later row from its predecessor.
            if (!isFirst || i != seam)
            {
                var linkOk = isFirst
                    ? record.PreviousHash.Length == 0
                    : string.Equals(record.PreviousHash, expectedPreviousHash, StringComparison.Ordinal);

                if (!linkOk)
                {
                    return new AuditVerification(false, record.Sequence, null, breakAt);
                }
            }

            expectedPreviousHash = record.RecordHash;
            headSequence = record.Sequence;
            headHash = record.RecordHash;
            isFirst = false;
        }

        // The chain is internally consistent. That is exactly what a truncated log also looks
        // like, so the external anchor is what actually detects a removed tail.
        AuditAnchor? anchor = _anchor.Read();
        if (anchor is not null)
        {
            if (anchor.Sequence > headSequence)
            {
                return new AuditVerification(
                    false, headSequence + 1,
                    $"Audit log ends at {headSequence} but the anchor records {anchor.Sequence}; "
                    + "records have been removed.", breakAt);
            }

            if (anchor.Sequence == headSequence
                && !string.Equals(anchor.RecordHash, headHash, StringComparison.Ordinal))
            {
                return new AuditVerification(
                    false, headSequence, "Head record does not match the external anchor.", breakAt);
            }
        }

        return new AuditVerification(true, null, null, breakAt);
    }

    /// <summary>One stored row, read before verification so the log can be examined in both directions.</summary>
    private sealed record StoredRecord(
        long Sequence, string RecordId, string CreatedAt, string PreviousHash, string RecordHash,
        AuditEntry Entry);

    private async Task<IReadOnlyList<StoredRecord>> ReadChainAsync(CancellationToken ct)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, record_id, principal_client_id, principal_os_user,
                   action_id, input_hash, outcome, created_at_utc, previous_hash, record_hash,
                   risk, via, decision, policy_ids, reason
            FROM audit_record
            ORDER BY sequence ASC;
            """;

        var records = new List<StoredRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            records.Add(new StoredRecord(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(7),
                reader.GetString(8), reader.GetString(9),
                new AuditEntry(
                    reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetString(5), reader.GetString(6),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13),
                    reader.IsDBNull(14) ? null : reader.GetString(14))));
        }

        return records;
    }

    /// <summary>
    /// Signs the record. <see cref="PreimageVersion"/> is part of the pre-image so that a future
    /// field addition is a recognisable format change rather than a silent verification failure.
    /// </summary>
    private string ComputeRecordHash(
        string previousHash, long sequence, string recordId, AuditEntry entry, string createdAt)
    {
        var preimage = string.Join(
            UnitSeparator,
            new[]
            {
                PreimageVersion,
                previousHash,
                sequence.ToString(CultureInfo.InvariantCulture),
                recordId,
                entry.PrincipalClientId,
                entry.PrincipalOsUser,
                entry.ActionId,
                entry.InputHash,
                entry.Outcome,
                createdAt,
                entry.Risk ?? string.Empty,
                entry.Via ?? string.Empty,
                entry.Decision ?? string.Empty,
                entry.PolicyIds ?? string.Empty,
                entry.Reason ?? string.Empty,
            });
        return Hashing.HmacSha256Hex(_key, preimage);
    }
}
