using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Cryptography;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Memories;

/// <summary>Persistent memory with provenance and audited revisions (RFC 03).</summary>
public sealed class SqliteMemoryService : IMemoryService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly IMemoryRanker _ranker;
    private readonly IClock _clock;

    public SqliteMemoryService(SqliteConnectionFactory factory, IMemoryRanker ranker, IClock clock)
    {
        _factory = factory;
        _ranker = ranker;
        _clock = clock;
    }

    public async Task<MemoryRecord> RecordAsync(
        MemoryCandidate candidate, MemoryProvenance provenance, CancellationToken ct)
    {
        // Rule 1: without an origin and an access policy it is not persisted at all. This is the
        // whole difference between a memory and a rumour.
        if (provenance.SourceRefs.Count == 0)
        {
            throw new MemoryException("A memory must declare where it came from.");
        }

        if (string.IsNullOrWhiteSpace(provenance.AccessPolicyId))
        {
            throw new MemoryException("A memory must declare an access policy.");
        }

        if (!Sensitivity.IsKnown(candidate.SensitivityClass))
        {
            throw new MemoryException($"Unknown sensitivity '{candidate.SensitivityClass}'.");
        }

        // Rule 5: sensitive material is not consolidated without the specific rule that permits it.
        if (Sensitivity.RequiresReference(candidate.SensitivityClass)
            && string.IsNullOrWhiteSpace(provenance.SpecificRuleRef))
        {
            throw new MemoryException(
                $"Consolidating {candidate.SensitivityClass} material needs a specific rule reference.");
        }

        // A fact inferred without confirmation starts as a candidate; only a person's own statement
        // begins active. Candidates may guide questions, never high-impact actions.
        var status = provenance.CreatedBy == MemoryOrigin.User
            ? MemoryStatus.Active
            : MemoryStatus.Candidate;

        var id = Guid.NewGuid().ToString("N");
        var memory = new MemoryRecord(
            id, candidate.Kind, candidate.SubjectRef, candidate.Predicate, candidate.ObjectJson,
            candidate.Summary, provenance.SourceRefs, provenance.EvidenceRefs,
            Math.Clamp(candidate.Confidence, 0, 1), status, candidate.SensitivityClass,
            provenance.AccessPolicyId, candidate.ValidFromUtc, candidate.ValidToUtc,
            candidate.RetentionUntilUtc, EmbeddingRef: null, provenance.CreatedBy,
            ContentHash: string.Empty);

        memory = memory with { ContentHash = HashOf(memory) };

        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO memory
                    (id, kind, subject_ref, predicate, object_json, summary, source_refs, evidence_refs,
                     confidence, status, sensitivity, access_policy_id, valid_from_utc, valid_to_utc,
                     retention_until_utc, embedding_ref, created_by, content_hash)
                VALUES (@id, @kind, @subject, @pred, @obj, @summary, @sources, @evidence, @conf,
                        @status, @sens, @policy, @from, @to, @retain, NULL, @by, @hash);
                """;
            Bind(command, memory);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await AppendRevisionAsync(
            id, RevisionOperation.Create, provenance.CreatedBy, "recorded", null, memory.ContentHash, ct)
            .ConfigureAwait(false);

        await MarkContradictionsAsync(memory, ct).ConfigureAwait(false);

        return (await GetAsync(id, ct).ConfigureAwait(false))!;
    }

    public async Task<MemorySearchResult> SearchAsync(
        string query, MemoryAccessContext access, MemoryFilters filters, CancellationToken ct)
    {
        // Access and classification are applied here, in the query, before anything ranks. A record
        // the caller may not see never reaches the ranker at all.
        var permitted = await PermittedAsync(access, filters, ct).ConfigureAwait(false);

        try
        {
            return new MemorySearchResult(_ranker.Rank(query, permitted), Confident: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Index failure: fall back to the structured result and say so. Reporting "no memories"
            // from a degraded search would be asserting an absence we cannot vouch for.
            return new MemorySearchResult(
                permitted.Select(m => new RankedMemory(m, 0)).ToList(),
                Confident: false,
                Degradation: $"Ranking unavailable ({ex.GetType().Name}); structured results only.");
        }
    }

    public async Task<MemoryRevision> ReviseAsync(
        string memoryId, string operation, string actor, string reason, CancellationToken ct)
    {
        MemoryRecord memory = await GetAsync(memoryId, ct).ConfigureAwait(false)
            ?? throw new MemoryException("Unknown memory.");

        // Rule 3: an owner's correction prevails over automatic inference. Once a person has
        // corrected a memory, the system does not quietly correct it back.
        if (operation == RevisionOperation.Correct && actor != MemoryOrigin.User)
        {
            IReadOnlyList<MemoryRevision> history = await RevisionsAsync(memoryId, ct).ConfigureAwait(false);
            if (history.Any(r => r.Operation == RevisionOperation.Correct && r.Actor == MemoryOrigin.User))
            {
                throw new MemoryException(
                    "This memory carries an owner correction; automatic inference cannot override it.");
            }
        }

        var status = operation switch
        {
            RevisionOperation.Confirm => MemoryStatus.Active,
            RevisionOperation.Correct => MemoryStatus.Active,
            RevisionOperation.Merge => MemoryStatus.Superseded,
            RevisionOperation.Retract => MemoryStatus.Retracted,
            RevisionOperation.Expire => MemoryStatus.Expired,
            _ => memory.Status,
        };

        MemoryRecord updated = memory with { Status = status };
        updated = updated with { ContentHash = HashOf(updated) };

        await SetStatusAsync(memoryId, status, updated.ContentHash, ct).ConfigureAwait(false);

        return await AppendRevisionAsync(
            memoryId, operation, actor, reason, memory.ContentHash, updated.ContentHash, ct)
            .ConfigureAwait(false);
    }

    public async Task<MemoryTombstone> ForgetAsync(string memoryId, string actor, CancellationToken ct)
    {
        MemoryRevision revision = await ReviseAsync(
            memoryId, RevisionOperation.Retract, actor, "forget requested", ct).ConfigureAwait(false);

        // Rule 4: a retraction leaves the audit trail intact but stops the memory being reachable
        // for normal reasoning. Telling the caller the real scope matters more than sounding final.
        return new MemoryTombstone(
            memoryId,
            revision,
            RemovedFromActiveIndexes: true,
            Scope: "Removed from search and reasoning. The record and its revision history remain "
                 + "for audit and are not recoverable for normal use.");
    }

    public async Task<MemoryRecord?> GetAsync(string memoryId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE id = @id;";
        command.Parameters.AddWithValue("@id", memoryId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<MemoryRevision>> RevisionsAsync(string memoryId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, memory_id, operation, actor, reason, prior_hash, new_hash, at_utc
              FROM memory_revision WHERE memory_id = @id ORDER BY at_utc ASC, rowid ASC;
            """;
        command.Parameters.AddWithValue("@id", memoryId);

        var rows = new List<MemoryRevision>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new MemoryRevision(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6), reader.GetString(7)));
        }

        return rows;
    }

    // ---- internals ----

    private async Task<IReadOnlyList<MemoryRecord>> PermittedAsync(
        MemoryAccessContext access, MemoryFilters filters, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var statuses = filters.IncludeCandidates
            ? new[] { MemoryStatus.Active, MemoryStatus.Disputed, MemoryStatus.Candidate }
            : [MemoryStatus.Active, MemoryStatus.Disputed];

        var statusPlaceholders = string.Join(',', statuses.Select((_, i) => "@s" + i));
        var policyPlaceholders = access.AccessPolicyIds.Count == 0
            ? "''"
            : string.Join(',', access.AccessPolicyIds.Select((_, i) => "@p" + i));

        // WORKING is ephemeral and never enters lasting research.
        command.CommandText = $"""
            {Select}
             WHERE kind <> @working
               AND status IN ({statusPlaceholders})
               AND access_policy_id IN ({policyPlaceholders})
               AND (@kind IS NULL OR kind = @kind)
               AND (@subject IS NULL OR subject_ref = @subject);
            """;
        command.Parameters.AddWithValue("@working", MemoryKind.Working);
        for (var i = 0; i < statuses.Length; i++)
        {
            command.Parameters.AddWithValue("@s" + i, statuses[i]);
        }

        for (var i = 0; i < access.AccessPolicyIds.Count; i++)
        {
            command.Parameters.AddWithValue("@p" + i, access.AccessPolicyIds[i]);
        }

        command.Parameters.AddWithValue("@kind", (object?)filters.Kind ?? DBNull.Value);
        command.Parameters.AddWithValue("@subject", (object?)filters.SubjectRef ?? DBNull.Value);

        var ceiling = Sensitivity.Rank(access.MaxSensitivity);
        var rows = new List<MemoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            MemoryRecord memory = Read(reader);
            if (Sensitivity.Rank(memory.SensitivityClass) <= ceiling)
            {
                rows.Add(memory);
            }
        }

        return rows;
    }

    /// <summary>
    /// Contradictory memories are both kept and both marked DISPUTED (RFC 03 limit case). Silently
    /// superseding one would destroy the evidence that the two ever disagreed.
    /// </summary>
    /// <remarks>
    /// Only memories whose validity windows <b>overlap</b> can contradict each other. Someone who
    /// lived in one city and then another is not contradicting themselves, and marking that pair
    /// DISPUTED would both be wrong and stop the graph recording the succession (RFC 04 rule 3).
    /// </remarks>
    private async Task MarkContradictionsAsync(MemoryRecord memory, CancellationToken ct)
    {
        if (memory.Status != MemoryStatus.Active)
        {
            return;
        }

        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, valid_from_utc, valid_to_utc FROM memory
             WHERE subject_ref = @subject AND predicate = @pred AND id <> @id
               AND status = @active AND object_json <> @obj;
            """;
        command.Parameters.AddWithValue("@subject", memory.SubjectRef);
        command.Parameters.AddWithValue("@pred", memory.Predicate);
        command.Parameters.AddWithValue("@id", memory.Id);
        command.Parameters.AddWithValue("@active", MemoryStatus.Active);
        command.Parameters.AddWithValue("@obj", memory.ObjectJson);

        var conflicting = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var otherFrom = reader.IsDBNull(1) ? null : reader.GetString(1);
                var otherTo = reader.IsDBNull(2) ? null : reader.GetString(2);

                if (Overlaps(memory.ValidFromUtc, memory.ValidToUtc, otherFrom, otherTo))
                {
                    conflicting.Add(reader.GetString(0));
                }
            }
        }

        foreach (var otherId in conflicting)
        {
            await SetStatusOnlyAsync(otherId, MemoryStatus.Disputed, ct).ConfigureAwait(false);
        }

        if (conflicting.Count > 0)
        {
            await SetStatusOnlyAsync(memory.Id, MemoryStatus.Disputed, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Half-open windows; a null bound is unbounded on that side.</summary>
    private static bool Overlaps(string? aFrom, string? aTo, string? bFrom, string? bTo)
    {
        DateTimeOffset from1 = Parse(aFrom) ?? DateTimeOffset.MinValue;
        DateTimeOffset to1 = Parse(aTo) ?? DateTimeOffset.MaxValue;
        DateTimeOffset from2 = Parse(bFrom) ?? DateTimeOffset.MinValue;
        DateTimeOffset to2 = Parse(bTo) ?? DateTimeOffset.MaxValue;

        return from1 < to2 && from2 < to1;
    }

    private static DateTimeOffset? Parse(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private async Task<MemoryRevision> AppendRevisionAsync(
        string memoryId, string operation, string actor, string reason,
        string? priorHash, string newHash, CancellationToken ct)
    {
        var revision = new MemoryRevision(
            Guid.NewGuid().ToString("N"), memoryId, operation, actor, reason, priorHash, newHash,
            _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory_revision (id, memory_id, operation, actor, reason, prior_hash, new_hash, at_utc)
            VALUES (@id, @mid, @op, @actor, @reason, @prior, @new, @at);
            """;
        command.Parameters.AddWithValue("@id", revision.Id);
        command.Parameters.AddWithValue("@mid", memoryId);
        command.Parameters.AddWithValue("@op", operation);
        command.Parameters.AddWithValue("@actor", actor);
        command.Parameters.AddWithValue("@reason", reason);
        command.Parameters.AddWithValue("@prior", (object?)priorHash ?? DBNull.Value);
        command.Parameters.AddWithValue("@new", newHash);
        command.Parameters.AddWithValue("@at", revision.AtUtc);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return revision;
    }

    private Task SetStatusAsync(string id, string status, string hash, CancellationToken ct) =>
        ExecuteAsync("UPDATE memory SET status = @s, content_hash = @h WHERE id = @id;", ct,
            ("@s", status), ("@h", hash), ("@id", id));

    private Task SetStatusOnlyAsync(string id, string status, CancellationToken ct) =>
        ExecuteAsync("UPDATE memory SET status = @s WHERE id = @id;", ct, ("@s", status), ("@id", id));

    private async Task ExecuteAsync(string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((var name, var value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private const string Select = """
        SELECT id, kind, subject_ref, predicate, object_json, summary, source_refs, evidence_refs,
               confidence, status, sensitivity, access_policy_id, valid_from_utc, valid_to_utc,
               retention_until_utc, embedding_ref, created_by, content_hash
          FROM memory
        """;

    private static void Bind(SqliteCommand command, MemoryRecord m)
    {
        command.Parameters.AddWithValue("@id", m.Id);
        command.Parameters.AddWithValue("@kind", m.Kind);
        command.Parameters.AddWithValue("@subject", m.SubjectRef);
        command.Parameters.AddWithValue("@pred", m.Predicate);
        command.Parameters.AddWithValue("@obj", m.ObjectJson);
        command.Parameters.AddWithValue("@summary", m.Summary);
        command.Parameters.AddWithValue("@sources", string.Join(',', m.SourceRefs));
        command.Parameters.AddWithValue("@evidence", string.Join(',', m.EvidenceRefs));
        command.Parameters.AddWithValue("@conf", m.Confidence);
        command.Parameters.AddWithValue("@status", m.Status);
        command.Parameters.AddWithValue("@sens", m.SensitivityClass);
        command.Parameters.AddWithValue("@policy", m.AccessPolicyId);
        command.Parameters.AddWithValue("@from", (object?)m.ValidFromUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("@to", (object?)m.ValidToUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("@retain", (object?)m.RetentionUntilUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("@by", m.CreatedBy);
        command.Parameters.AddWithValue("@hash", m.ContentHash);
    }

    private static MemoryRecord Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        r.GetString(6).Split(',', StringSplitOptions.RemoveEmptyEntries),
        r.GetString(7).Split(',', StringSplitOptions.RemoveEmptyEntries),
        r.GetDouble(8), r.GetString(9), r.GetString(10), r.GetString(11),
        r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13),
        r.IsDBNull(14) ? null : r.GetString(14), r.IsDBNull(15) ? null : r.GetString(15),
        r.GetString(16), r.GetString(17));

    private static string HashOf(MemoryRecord m) => Hashing.Sha256Hex(string.Join(
        (char)0x1F,
        new[]
        {
            m.Id, m.Kind, m.SubjectRef, m.Predicate, m.ObjectJson, m.Summary,
            string.Join(',', m.SourceRefs), string.Join(',', m.EvidenceRefs),
            m.Confidence.ToString("R", CultureInfo.InvariantCulture), m.Status,
            m.SensitivityClass, m.AccessPolicyId, m.CreatedBy,
        }));
}
