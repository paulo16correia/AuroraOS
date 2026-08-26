using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Minds;

/// <summary>
/// The Mind aggregate over SQLite, with the propose/validate/apply discipline RFC 020 rule 2 asks
/// for.
/// </summary>
/// <remarks>
/// Applying happens inside one transaction over both tables — the Mind's row and the change set's
/// status move together, or neither moves. That is what "atomic per aggregate" means here, and it
/// is why the apply path builds the whole new row before writing any of it.
/// </remarks>
public sealed class SqliteMindService : IMindService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly IClock _clock;

    public SqliteMindService(SqliteConnectionFactory factory, IClock clock)
    {
        _factory = factory;
        _clock = clock;
    }

    public async Task<Mind> OpenAsync(string tenantId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using (SqliteCommand read = connection.CreateCommand())
        {
            read.CommandText = Select + " WHERE tenant_id = @t;";
            read.Parameters.AddWithValue("@t", tenantId);

            await using SqliteDataReader reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return Read(reader);
            }
        }

        var now = Iso(_clock.UtcNow);

        // ACTIVE from the start: by the time anything asks for the Mind, the schema is created and
        // every service behind it is constructed. Bootstrapping is InstanceState's business
        // (RFC 039) and describing it here as well would give it two answers.
        var mind = new Mind(
            Guid.NewGuid().ToString("N"), tenantId, MindStatus.Active,
            SelfModelId: null, IdentityId: null,
            PolicySetVersion: "0", WorldModelVersion: "0",
            LastConsolidationAtUtc: null, now, now);

        await using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO mind
                (id, tenant_id, status, self_model_id, identity_id, policy_set_version,
                 world_model_version, last_consolidation_at_utc, created_at_utc, updated_at_utc,
                 paused_by, paused_reason)
            VALUES (@id, @t, @s, NULL, NULL, @pv, @wv, NULL, @c, @u, NULL, NULL);
            """;

        insert.Parameters.AddWithValue("@id", mind.Id);
        insert.Parameters.AddWithValue("@t", mind.TenantId);
        insert.Parameters.AddWithValue("@s", mind.Status);
        insert.Parameters.AddWithValue("@pv", mind.PolicySetVersion);
        insert.Parameters.AddWithValue("@wv", mind.WorldModelVersion);
        insert.Parameters.AddWithValue("@c", now);
        insert.Parameters.AddWithValue("@u", now);

        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return mind;
    }

    public async Task<Mind?> GetAsync(string mindId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = Select + " WHERE id = @id;";
        command.Parameters.AddWithValue("@id", mindId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<MindChangeSet> ProposeAsync(MindChangeSet draft, CancellationToken ct)
    {
        if (draft.Changes.Count == 0)
        {
            throw new MindException("A change set that changes nothing is not a proposal.");
        }

        if (!MindChangeSource.IsKnown(draft.Source))
        {
            // Rule 1 is about knowing who wrote to Mind. An unrecognised source is an answer
            // nobody can act on later, so it is refused at the door rather than stored.
            throw new MindException($"Unknown change source '{draft.Source}'.");
        }

        _ = await GetAsync(draft.MindId, ct).ConfigureAwait(false)
            ?? throw new MindException("Unknown mind.");

        var proposal = draft with
        {
            Id = Guid.NewGuid().ToString("N"),
            Status = MindChangeSetStatus.Proposed,
            CreatedAtUtc = Iso(_clock.UtcNow),
            Detail = null,
        };

        await SaveChangeSetAsync(proposal, ct).ConfigureAwait(false);
        return proposal;
    }

    public async Task<MindChangeSet> ValidateAsync(string changeSetId, CancellationToken ct)
    {
        MindChangeSet set = await RequireChangeSetAsync(changeSetId, ct).ConfigureAwait(false);

        if (set.Status != MindChangeSetStatus.Proposed)
        {
            throw new MindException($"Only a PROPOSED change set is validated; this is {set.Status}.");
        }

        var refusals = new List<string>();

        if (set.EvidenceRefs.Count == 0)
        {
            // LAW-001 in the Mind's own terms: nothing enters without something behind it.
            refusals.Add("no evidence");
        }

        foreach (MindChange change in set.Changes.Where(c => !MindField.IsKnown(c.Field)))
        {
            refusals.Add($"'{change.Field}' is not a field of the Mind");
        }

        foreach (MindChange change in set.Changes.Where(c => string.IsNullOrWhiteSpace(c.Value)))
        {
            // Clearing a field is a change somebody should have to say out loud, and an empty
            // string arriving by accident looks exactly like one arriving on purpose.
            refusals.Add($"'{change.Field}' would be set to nothing");
        }

        var duplicated = set.Changes
            .GroupBy(c => c.Field, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        foreach (var field in duplicated)
        {
            // Which of the two wins would depend on the order they were listed in, which is not a
            // decision anybody made.
            refusals.Add($"'{field}' is changed twice in one set");
        }

        MindChangeSet decided = refusals.Count > 0
            ? set with
            {
                Status = MindChangeSetStatus.Rejected,
                Detail = string.Join("; ", refusals),
            }
            : set with { Status = MindChangeSetStatus.Validated };

        await SaveChangeSetAsync(decided, ct).ConfigureAwait(false);
        return decided;
    }

    public async Task<Mind> ApplyAsync(string changeSetId, CancellationToken ct)
    {
        MindChangeSet set = await RequireChangeSetAsync(changeSetId, ct).ConfigureAwait(false);

        if (set.Status != MindChangeSetStatus.Validated)
        {
            throw new MindException($"Only a VALIDATED change set is applied; this is {set.Status}.");
        }

        Mind mind = await GetAsync(set.MindId, ct).ConfigureAwait(false)
            ?? throw new MindException("Unknown mind.");

        // Built whole before anything is written, so a field that cannot be applied is found while
        // the Mind is still untouched rather than halfway through changing it.
        Mind updated = set.Changes.Aggregate(mind, Apply) with { UpdatedAtUtc = Iso(_clock.UtcNow) };

        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            await WriteMindAsync(connection, transaction, updated, ct).ConfigureAwait(false);

            await WriteChangeSetStatusAsync(
                connection, transaction, set.Id, MindChangeSetStatus.Applied, null, ct)
                .ConfigureAwait(false);

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return updated;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);

            // Rule 2's "never partially silent": the Mind is back where it was and the change set
            // says so, rather than the failure being visible only as an exception somebody logged.
            await WriteChangeSetStatusAsync(
                connection, null, set.Id, MindChangeSetStatus.RolledBack,
                $"apply failed: {failure.GetType().Name}", ct).ConfigureAwait(false);

            throw new MindException($"The change set was rolled back: {failure.Message}");
        }
    }

    private static Mind Apply(Mind mind, MindChange change) => change.Field switch
    {
        MindField.SelfModelId => mind with { SelfModelId = change.Value },
        MindField.IdentityId => mind with { IdentityId = change.Value },
        MindField.PolicySetVersion => mind with { PolicySetVersion = change.Value },
        MindField.WorldModelVersion => mind with { WorldModelVersion = change.Value },
        MindField.LastConsolidationAt => mind with { LastConsolidationAtUtc = change.Value },
        _ => throw new MindException($"'{change.Field}' is not a field of the Mind."),
    };

    public async Task<MindChangeSet?> ChangeSetAsync(string changeSetId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, mind_id, source, changes_json, evidence_refs, policy_decision_ids, status,
                   created_at_utc, detail
              FROM mind_change_set WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@id", changeSetId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new MindChangeSet(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            AuroraJson.Deserialize<List<MindChange>>(reader.GetString(3)) ?? [],
            Split(reader.GetString(4)), Split(reader.GetString(5)),
            reader.GetString(6), reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8));
    }

    public Task<Mind> PauseAsync(string mindId, string actor, string reason, CancellationToken ct) =>
        SetPausedAsync(mindId, MindStatus.Paused, actor, reason, ct);

    public Task<Mind> ResumeAsync(string mindId, string actor, CancellationToken ct) =>
        SetPausedAsync(mindId, MindStatus.Active, null, null, ct);

    public async Task<Mind> RetireAsync(
        string mindId, string actor, string reason, CancellationToken ct)
    {
        Mind mind = await GetAsync(mindId, ct).ConfigureAwait(false)
            ?? throw new MindException("Unknown mind.");

        if (mind.Status == MindStatus.Retired)
        {
            throw new MindException("This Mind is already retired.");
        }

        if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(reason))
        {
            throw new MindException("Retiring a Mind records who did it and why.");
        }

        Mind retired = mind with
        {
            Status = MindStatus.Retired,
            PausedBy = actor,
            PausedReason = reason,
            UpdatedAtUtc = Iso(_clock.UtcNow),
        };

        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await WriteMindAsync(connection, null, retired, ct).ConfigureAwait(false);
        return retired;
    }

    private async Task<Mind> SetPausedAsync(
        string mindId, string status, string? actor, string? reason, CancellationToken ct)
    {
        Mind mind = await GetAsync(mindId, ct).ConfigureAwait(false)
            ?? throw new MindException("Unknown mind.");

        // Terminal means terminal. Pausing or resuming a retired Mind would be operating on an
        // entity its owner has finished with.
        if (mind.Status == MindStatus.Retired)
        {
            throw new MindException("This Mind is retired.");
        }

        if (status == MindStatus.Paused && string.IsNullOrWhiteSpace(reason))
        {
            throw new MindException("Pausing the Mind is recorded with a reason.");
        }

        Mind updated = mind with
        {
            Status = status,
            PausedBy = actor,
            PausedReason = reason,
            UpdatedAtUtc = Iso(_clock.UtcNow),
        };

        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await WriteMindAsync(connection, null, updated, ct).ConfigureAwait(false);
        return updated;
    }

    private static async Task WriteMindAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Mind mind, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE mind SET status = @s, self_model_id = @self, identity_id = @ident,
                            policy_set_version = @pv, world_model_version = @wv,
                            last_consolidation_at_utc = @cons, updated_at_utc = @u,
                            paused_by = @by, paused_reason = @why
             WHERE id = @id;
            """;

        command.Parameters.AddWithValue("@s", mind.Status);
        command.Parameters.AddWithValue("@self", (object?)mind.SelfModelId ?? DBNull.Value);
        command.Parameters.AddWithValue("@ident", (object?)mind.IdentityId ?? DBNull.Value);
        command.Parameters.AddWithValue("@pv", mind.PolicySetVersion);
        command.Parameters.AddWithValue("@wv", mind.WorldModelVersion);
        command.Parameters.AddWithValue("@cons", (object?)mind.LastConsolidationAtUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("@u", mind.UpdatedAtUtc);
        command.Parameters.AddWithValue("@by", (object?)mind.PausedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("@why", (object?)mind.PausedReason ?? DBNull.Value);
        command.Parameters.AddWithValue("@id", mind.Id);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<MindChangeSet> RequireChangeSetAsync(string id, CancellationToken ct) =>
        await ChangeSetAsync(id, ct).ConfigureAwait(false)
        ?? throw new MindException("Unknown change set.");

    private async Task SaveChangeSetAsync(MindChangeSet set, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mind_change_set
                (id, mind_id, source, changes_json, evidence_refs, policy_decision_ids, status,
                 created_at_utc, detail)
            VALUES (@id, @m, @src, @changes, @ev, @pol, @status, @at, @detail)
            ON CONFLICT(id) DO UPDATE SET status = excluded.status, detail = excluded.detail;
            """;

        command.Parameters.AddWithValue("@id", set.Id);
        command.Parameters.AddWithValue("@m", set.MindId);
        command.Parameters.AddWithValue("@src", set.Source);
        command.Parameters.AddWithValue("@changes", AuroraJson.Serialize(set.Changes));
        command.Parameters.AddWithValue("@ev", string.Join(',', set.EvidenceRefs));
        command.Parameters.AddWithValue("@pol", string.Join(',', set.PolicyDecisionIds));
        command.Parameters.AddWithValue("@status", set.Status);
        command.Parameters.AddWithValue("@at", set.CreatedAtUtc);
        command.Parameters.AddWithValue("@detail", (object?)set.Detail ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteChangeSetStatusAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string id, string status,
        string? detail, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE mind_change_set SET status = @s, detail = @d WHERE id = @id;";
        command.Parameters.AddWithValue("@s", status);
        command.Parameters.AddWithValue("@d", (object?)detail ?? DBNull.Value);
        command.Parameters.AddWithValue("@id", id);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private const string Select = """
        SELECT id, tenant_id, status, self_model_id, identity_id, policy_set_version,
               world_model_version, last_consolidation_at_utc, created_at_utc, updated_at_utc,
               paused_by, paused_reason
          FROM mind
        """;

    private static Mind Read(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetString(5), reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.GetString(8), reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11));

    private static IReadOnlyList<string> Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries);

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
