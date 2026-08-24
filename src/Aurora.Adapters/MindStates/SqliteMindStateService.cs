using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Vault;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.MindStates;

/// <summary>
/// Mind State capture, verification and recovery (RFC 043).
/// </summary>
/// <remarks>
/// A snapshot is not a database dump. It is a coherent set of references, encrypted and
/// authenticated, pinned to an audit position — enough to resume an operational entity rather than
/// merely to remember things.
/// </remarks>
public sealed class SqliteMindStateService : IMindStateService
{
    /// <summary>Layout of the encrypted body. A newer one is not deserialized permissively.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly string[] NeverExportedSchemes = ["vault://", "local://"];

    private readonly SqliteConnectionFactory _factory;
    private readonly AesGcmSecretProtector _protector;
    private readonly IClock _clock;
    private readonly IAuditStore _audit;
    private readonly IIdempotencyStore _idempotency;
    private readonly IConsentSessionStore _sessions;
    private readonly IInstanceLifecycle _lifecycle;

    public SqliteMindStateService(
        SqliteConnectionFactory factory,
        AesGcmSecretProtector protector,
        IClock clock,
        IAuditStore audit,
        IIdempotencyStore idempotency,
        IConsentSessionStore sessions,
        IInstanceLifecycle lifecycle)
    {
        _factory = factory;
        _protector = protector;
        _clock = clock;
        _audit = audit;
        _idempotency = idempotency;
        _sessions = sessions;
        _lifecycle = lifecycle;
    }

    public async Task<MindStateSnapshot> CaptureAsync(
        string mindId, MindStateComponents components, ConsistencyLevel level, CancellationToken ct)
    {
        // Rule 1: never pretend atomicity that does not exist. Strict refuses; best-effort captures
        // and names what was not consistent, so a reader always knows which they are holding.
        if (level == ConsistencyLevel.Strict && components.NonConsistentComponents.Count > 0)
        {
            throw new MindStateException(
                "Strict capture requires every component to be consistent; "
                + $"{string.Join(", ", components.NonConsistentComponents)} were not.");
        }

        var id = Guid.NewGuid().ToString("N");
        var capturedAt = _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var body = JsonSerializer.Serialize(components);
        SealedSecret sealedBody = _protector.Protect(id, body);
        var anchor = await _audit.HeadHashAsync(ct).ConfigureAwait(false);

        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mind_state_snapshot
                (id, mind_id, schema_version, captured_at_utc, consistency_cursor, audit_anchor_hash,
                 encryption_metadata, status, non_consistent_components, nonce, ciphertext, tag)
            VALUES (@id, @mind, @ver, @at, @cursor, @anchor, @enc, @status, @nc, @n, @c, @t);
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@mind", mindId);
        command.Parameters.AddWithValue("@ver", CurrentSchemaVersion);
        command.Parameters.AddWithValue("@at", capturedAt);
        command.Parameters.AddWithValue("@cursor", capturedAt);
        command.Parameters.AddWithValue("@anchor", (object?)anchor ?? DBNull.Value);
        command.Parameters.AddWithValue("@enc", "AES-256-GCM");
        command.Parameters.AddWithValue("@status", SnapshotStatus.Complete);
        command.Parameters.AddWithValue("@nc", string.Join(',', components.NonConsistentComponents));
        command.Parameters.AddWithValue("@n", sealedBody.Nonce);
        command.Parameters.AddWithValue("@c", sealedBody.Ciphertext);
        command.Parameters.AddWithValue("@t", sealedBody.Tag);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return (await GetAsync(id, ct).ConfigureAwait(false))!;
    }

    public async Task<VerificationReport> VerifyAsync(string snapshotId, CancellationToken ct)
    {
        MindStateSnapshot snapshot = await GetAsync(snapshotId, ct).ConfigureAwait(false)
            ?? throw new MindStateException("Unknown snapshot.");

        // A body written by a newer layout is not read permissively; that is a migration, not a
        // best guess at what the unknown fields meant.
        if (snapshot.SchemaVersion > CurrentSchemaVersion)
        {
            return new VerificationReport(
                snapshotId, snapshot.Status,
                $"Snapshot is schema v{snapshot.SchemaVersion}; this build reads up to "
                + $"v{CurrentSchemaVersion}. A versioned migration is required.");
        }

        try
        {
            _ = await ReadComponentsAsync(snapshotId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            await SetStatusAsync(snapshotId, SnapshotStatus.Corrupt, ct).ConfigureAwait(false);
            return new VerificationReport(snapshotId, SnapshotStatus.Corrupt, "Body failed authentication.");
        }

        await SetStatusAsync(snapshotId, SnapshotStatus.Verified, ct).ConfigureAwait(false);
        return new VerificationReport(snapshotId, SnapshotStatus.Verified);
    }

    public async Task<RecoveryPlan> RestoreAsync(
        string snapshotId, string targetEnvironment, string instanceId, CancellationToken ct)
    {
        MindStateSnapshot snapshot = await GetAsync(snapshotId, ct).ConfigureAwait(false)
            ?? throw new MindStateException("Unknown snapshot.");

        if (snapshot.Status == SnapshotStatus.Corrupt)
        {
            // Never partially restore a corrupt snapshot; the last VERIFIED one is the fallback.
            throw new MindStateException(
                "Snapshot is CORRUPT; restore the last VERIFIED snapshot instead.");
        }

        if (snapshot.Status != SnapshotStatus.Verified)
        {
            VerificationReport report = await VerifyAsync(snapshotId, ct).ConfigureAwait(false);
            if (report.Status != SnapshotStatus.Verified)
            {
                throw new MindStateException($"Snapshot did not verify: {report.Detail}");
            }
        }

        // Rule 2, in order: RECOVERING first, then revoke temporary leases, then reconcile.
        await _lifecycle.GetOrCreateAsync(instanceId, ct).ConfigureAwait(false);
        await _lifecycle.TransitionAsync(
            instanceId, InstanceState.Recovering, TransitionActor.Kernel,
            $"restoring snapshot {snapshotId}", ct: ct).ConfigureAwait(false);

        var revoked = await _sessions.RevokeAllAsync(ct).ConfigureAwait(false);
        IReadOnlyList<string> unresolved = await _idempotency.ListUnknownAsync(ct).ConfigureAwait(false);

        var steps = new List<string>
        {
            "move the instance to RECOVERING",
            $"revoke temporary leases ({revoked} consent session(s))",
            "re-evaluate expired deadlines, credentials, schedules and beliefs",
        };

        if (unresolved.Count > 0)
        {
            steps.Add($"reconcile {unresolved.Count} indeterminate tool call(s) before acting");
        }

        var plan = new RecoveryPlan(
            Guid.NewGuid().ToString("N"),
            snapshotId,
            targetEnvironment,
            steps,
            unresolved,
            ReconciliationPolicy: "consult-provider-before-retry",
            Status: unresolved.Count > 0 ? RecoveryStatus.WaitingReconciliation : RecoveryStatus.Planned);

        await using (SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO recovery_plan
                    (id, snapshot_id, target_environment, steps, unresolved_tool_call_refs,
                     reconciliation_policy, status, created_at_utc)
                VALUES (@id, @sid, @env, @steps, @unresolved, @policy, @status, @at);
                """;
            command.Parameters.AddWithValue("@id", plan.Id);
            command.Parameters.AddWithValue("@sid", plan.SnapshotId);
            command.Parameters.AddWithValue("@env", plan.TargetEnvironment);
            command.Parameters.AddWithValue("@steps", string.Join('\n', plan.Steps));
            command.Parameters.AddWithValue("@unresolved", string.Join(',', plan.UnresolvedToolCallRefs));
            command.Parameters.AddWithValue("@policy", plan.ReconciliationPolicy);
            command.Parameters.AddWithValue("@status", plan.Status);
            command.Parameters.AddWithValue("@at", _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // The snapshot is only RESTORED once nothing is left indeterminate; while a tool call is
        // UNKNOWN the restore is still in progress, whatever the process would like to believe.
        if (unresolved.Count == 0)
        {
            await SetStatusAsync(snapshotId, SnapshotStatus.Restored, ct).ConfigureAwait(false);
        }

        return plan;
    }

    public async Task<MindStateSnapshot?> LastVerifiedAsync(string mindId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SnapshotSelect
            + " WHERE mind_id = @mind AND status IN (@verified, @restored)"
            + " ORDER BY captured_at_utc DESC LIMIT 1;";
        command.Parameters.AddWithValue("@mind", mindId);
        command.Parameters.AddWithValue("@verified", SnapshotStatus.Verified);
        command.Parameters.AddWithValue("@restored", SnapshotStatus.Restored);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadSnapshot(reader) : null;
    }

    public async Task<RedactedExport> ExportAsync(
        string snapshotId, ExportAccessContext access, CancellationToken ct)
    {
        MindStateSnapshot snapshot = await GetAsync(snapshotId, ct).ConfigureAwait(false)
            ?? throw new MindStateException("Unknown snapshot.");
        MindStateComponents components = await ReadComponentsAsync(snapshotId, ct).ConfigureAwait(false);

        var sections = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var redacted = new List<string>();

        void Add(string name, IReadOnlyList<string> refs)
        {
            // Rule 4: Vault data is never exportable. Filtering by scheme means a reference that
            // points at a secret cannot ride along inside an otherwise innocent section.
            var kept = refs.Where(r =>
                !NeverExportedSchemes.Any(s => r.StartsWith(s, StringComparison.OrdinalIgnoreCase))).ToList();

            if (kept.Count != refs.Count)
            {
                redacted.Add($"{name} (vault references removed)");
            }

            sections[name] = kept;
        }

        Add("beliefs", components.BeliefRefs);
        Add("preferences", components.PreferenceRefs);
        Add("relationships", components.RelationshipRefs);
        Add("goals", components.GoalRefs);
        Add("plans", components.PlanRefs);
        Add("tool_state", components.ToolStateRefs);

        // Working memory is short-retention; it leaves only when the access context allows it.
        if (access.IncludeWorkingMemory)
        {
            Add("working_memory", components.WorkingMemoryRefs);
        }
        else
        {
            redacted.Add("working_memory (short retention policy)");
        }

        return new RedactedExport(snapshotId, snapshot.MindId, snapshot.CapturedAtUtc, sections, redacted);
    }

    public async Task<MindStateSnapshot?> GetAsync(string snapshotId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SnapshotSelect + " WHERE id = @id;";
        command.Parameters.AddWithValue("@id", snapshotId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadSnapshot(reader) : null;
    }

    private const string SnapshotSelect = """
        SELECT id, mind_id, schema_version, captured_at_utc, consistency_cursor, audit_anchor_hash,
               encryption_metadata, status, non_consistent_components
          FROM mind_state_snapshot
        """;

    private async Task<MindStateComponents> ReadComponentsAsync(string snapshotId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT nonce, ciphertext, tag FROM mind_state_snapshot WHERE id = @id;";
        command.Parameters.AddWithValue("@id", snapshotId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new MindStateException("Unknown snapshot.");
        }

        var sealedBody = new SealedSecret((byte[])reader[0], (byte[])reader[1], (byte[])reader[2]);
        var json = new string(_protector.Unprotect(snapshotId, sealedBody));

        return JsonSerializer.Deserialize<MindStateComponents>(json)
            ?? throw new JsonException("Snapshot body was empty.");
    }

    private async Task SetStatusAsync(string snapshotId, string status, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE mind_state_snapshot SET status = @s WHERE id = @id;";
        command.Parameters.AddWithValue("@s", status);
        command.Parameters.AddWithValue("@id", snapshotId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static MindStateSnapshot ReadSnapshot(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetString(7),
        r.GetString(8).Split(',', StringSplitOptions.RemoveEmptyEntries));
}
