using Aurora.Adapters.Lifecycle;
using Aurora.Adapters.MindStates;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Vault;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>Conformance tests for RFC 043.</summary>
public sealed class MindStateTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string MindId = "mind-1";
    private const string InstanceId = "instance-1";

    private static MindStateComponents Components(
        IReadOnlyList<string>? nonConsistent = null, IReadOnlyList<string>? toolState = null) => new(
        "identity/1", "personality/1", "self/1",
        BeliefRefs: ["belief/1"], PreferenceRefs: ["pref/1"], RelationshipRefs: ["rel/1"],
        GoalRefs: ["goal/1"], ActiveTaskRefs: ["task/1"], PlanRefs: ["plan/1"],
        AttentionStateRef: "attention/1", WorkingMemoryRefs: ["wm/1"],
        WorldModelVersion: "world/7",
        ToolStateRefs: toolState ?? ["tool/1"],
        SchedulerStateRefs: ["sched/1"], InteractionStateRef: "interaction/1",
        PolicySetVersion: "policy/1", HealthRef: "health/1",
        EffectiveGenomeRef: "genome-1",
        NonConsistentComponents: nonConsistent ?? []);

    private static (SqliteMindStateService Service, SqliteInstanceLifecycle Lifecycle,
        SqliteIdempotencyStore Idempotency, SqliteConsentSessionStore Sessions) New(SqliteTestDb db)
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var lifecycle = new SqliteInstanceLifecycle(db.Factory, clock);
        var idempotency = new SqliteIdempotencyStore(db.Factory, clock);
        var sessions = new SqliteConsentSessionStore(
            db.Factory, clock, new FakeServerIdentity("boot-1"),
            new VersionedFakePolicy(true, "pv-1"), ConsentSessionOptions.Default);

        var service = new SqliteMindStateService(
            db.Factory,
            new AesGcmSecretProtector(Enumerable.Repeat((byte)5, 32).ToArray()),
            clock,
            new SqliteAuditStore(db.Factory, clock, new byte[32], new AuditAnchorFile(
                Path.Combine(Path.GetTempPath(), $"anchor-{Guid.NewGuid():N}"))),
            idempotency,
            sessions,
            lifecycle);

        return (service, lifecycle, idempotency, sessions);
    }

    // ---- rule 1: never pretend atomicity ----

    [Fact]
    public async Task StrictCaptureRefusesAnInconsistentComponent()
    {
        using var db = new SqliteTestDb();
        var (service, _, _, _) = New(db);

        await Assert.ThrowsAsync<MindStateException>(() => service.CaptureAsync(
            MindId, Components(nonConsistent: ["world_model"]), ConsistencyLevel.Strict, Ct));
    }

    [Fact]
    public async Task BestEffortCaptureNamesWhatWasNotConsistent()
    {
        using var db = new SqliteTestDb();
        var (service, _, _, _) = New(db);

        MindStateSnapshot snapshot = await service.CaptureAsync(
            MindId, Components(nonConsistent: ["world_model"]), ConsistencyLevel.BestEffort, Ct);

        // Declaring it is the point: a reader must know which kind of snapshot they hold.
        Assert.Equal(["world_model"], snapshot.NonConsistentComponents);
        Assert.Equal(SnapshotStatus.Complete, snapshot.Status);
    }

    [Fact]
    public async Task ASnapshotPinsTheAuditPosition()
    {
        using var db = new SqliteTestDb();
        var (service, _, _, _) = New(db);
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var audit = new SqliteAuditStore(db.Factory, clock, new byte[32], new AuditAnchorFile(
            Path.Combine(Path.GetTempPath(), $"anchor-{Guid.NewGuid():N}")));
        await audit.AppendAsync(new AuditEntry("c1", "u1", "echo.say", "ih", "completed"), Ct);

        MindStateSnapshot snapshot = await service.CaptureAsync(
            MindId, Components(), ConsistencyLevel.Strict, Ct);

        Assert.False(string.IsNullOrEmpty(snapshot.AuditAnchorHash));
    }

    // ---- rule 3: encrypted and authenticated ----

    [Fact]
    public async Task TheBodyIsEncryptedAtRest()
    {
        using var db = new SqliteTestDb();
        var (service, _, _, _) = New(db);
        await service.CaptureAsync(MindId, Components(), ConsistencyLevel.Strict, Ct);

        using var connection = db.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ciphertext FROM mind_state_snapshot;";
        var ciphertext = System.Text.Encoding.UTF8.GetString((byte[])command.ExecuteScalar()!);

        Assert.DoesNotContain("identity/1", ciphertext, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGoodSnapshotVerifies()
    {
        using var db = new SqliteTestDb();
        var (service, _, _, _) = New(db);
        MindStateSnapshot snapshot = await service.CaptureAsync(
            MindId, Components(), ConsistencyLevel.Strict, Ct);

        VerificationReport report = await service.VerifyAsync(snapshot.Id, Ct);

        Assert.Equal(SnapshotStatus.Verified, report.Status);
    }

    [Fact]
    public async Task ATamperedBodyIsMarkedCorrupt()
    {
        using var db = new SqliteTestDb();
        var (service, _, _, _) = New(db);
        MindStateSnapshot snapshot = await service.CaptureAsync(
            MindId, Components(), ConsistencyLevel.Strict, Ct);

        using (var connection = db.Factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE mind_state_snapshot SET ciphertext = zeroblob(32);";
            command.ExecuteNonQuery();
        }

        VerificationReport report = await service.VerifyAsync(snapshot.Id, Ct);

        Assert.Equal(SnapshotStatus.Corrupt, report.Status);
    }

    [Fact]
    public async Task ACorruptSnapshotIsNeverPartiallyRestored()
    {
        using var db = new SqliteTestDb();
        var (service, _, _, _) = New(db);
        MindStateSnapshot snapshot = await service.CaptureAsync(
            MindId, Components(), ConsistencyLevel.Strict, Ct);

        using (var connection = db.Factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE mind_state_snapshot SET ciphertext = zeroblob(32);";
            command.ExecuteNonQuery();
        }

        await service.VerifyAsync(snapshot.Id, Ct);

        MindStateException error = await Assert.ThrowsAsync<MindStateException>(
            () => service.RestoreAsync(snapshot.Id, "local", InstanceId, Ct));

        Assert.Contains("VERIFIED", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLastVerifiedSnapshotIsTheFallback()
    {
        using var db = new SqliteTestDb();
        var (service, _, _, _) = New(db);
        MindStateSnapshot good = await service.CaptureAsync(
            MindId, Components(), ConsistencyLevel.Strict, Ct);
        await service.VerifyAsync(good.Id, Ct);

        MindStateSnapshot? fallback = await service.LastVerifiedAsync(MindId, Ct);

        Assert.Equal(good.Id, fallback!.Id);
    }

    // ---- rule 2: restore order ----

    [Fact]
    public async Task RestoreMovesTheInstanceToRecoveringAndRevokesLeases()
    {
        using var db = new SqliteTestDb();
        var (service, lifecycle, _, sessions) = New(db);
        await sessions.OpenAsync(new Principal("c1", "u1"), Ct);
        Assert.Equal(1, await sessions.CountActiveAsync(Ct));

        MindStateSnapshot snapshot = await service.CaptureAsync(
            MindId, Components(), ConsistencyLevel.Strict, Ct);

        RecoveryPlan plan = await service.RestoreAsync(snapshot.Id, "local", InstanceId, Ct);

        Assert.Equal(InstanceState.Recovering, (await lifecycle.GetOrCreateAsync(InstanceId, Ct)).State);
        Assert.Equal(0, await sessions.CountActiveAsync(Ct));
        Assert.Contains(plan.Steps, s => s.Contains("RECOVERING", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestoreWaitsWhileAToolCallIsIndeterminate()
    {
        using var db = new SqliteTestDb();
        var (service, _, idempotency, _) = New(db);
        var caller = new Principal("c1", "u1");

        // A request that reached EXECUTING and then died, reconciled to UNKNOWN.
        await idempotency.BeginAsync(caller, "k1", "hashA", Ct);
        await idempotency.MarkExecutingAsync(caller, "k1", Ct);
        var later = new SqliteIdempotencyStore(db.Factory, new TestClock(DateTimeOffset.UnixEpoch.AddHours(1)));
        await later.ReconcileStaleAsync(TimeSpan.FromMinutes(15), Ct);

        MindStateSnapshot snapshot = await service.CaptureAsync(
            MindId, Components(), ConsistencyLevel.Strict, Ct);

        RecoveryPlan plan = await service.RestoreAsync(snapshot.Id, "local", InstanceId, Ct);

        // "Was it sending an email when we restarted?" — the honest answer is not yet known.
        Assert.Equal(RecoveryStatus.WaitingReconciliation, plan.Status);
        Assert.Equal(["k1"], plan.UnresolvedToolCallRefs);
        Assert.Equal("consult-provider-before-retry", plan.ReconciliationPolicy);

        // The snapshot is not RESTORED while anything is still indeterminate.
        Assert.NotEqual(SnapshotStatus.Restored, (await service.GetAsync(snapshot.Id, Ct))!.Status);
    }

    [Fact]
    public async Task ACleanRestoreCompletesAndMarksTheSnapshotRestored()
    {
        using var db = new SqliteTestDb();
        var (service, _, _, _) = New(db);
        MindStateSnapshot snapshot = await service.CaptureAsync(
            MindId, Components(), ConsistencyLevel.Strict, Ct);

        RecoveryPlan plan = await service.RestoreAsync(snapshot.Id, "local", InstanceId, Ct);

        Assert.Equal(RecoveryStatus.Planned, plan.Status);
        Assert.Equal(SnapshotStatus.Restored, (await service.GetAsync(snapshot.Id, Ct))!.Status);
    }

    // ---- rule 4: export ----

    [Fact]
    public async Task ExportNeverCarriesVaultReferences()
    {
        using var db = new SqliteTestDb();
        var (service, _, _, _) = New(db);
        MindStateSnapshot snapshot = await service.CaptureAsync(
            MindId, Components(toolState: ["tool/1", "vault://secret-42", "local://secret-7"]),
            ConsistencyLevel.Strict, Ct);

        RedactedExport export = await service.ExportAsync(
            snapshot.Id, new ExportAccessContext("owner", IncludeWorkingMemory: true), Ct);

        Assert.Equal(["tool/1"], export.Sections["tool_state"]);
        Assert.Contains(export.RedactedSections, s => s.Contains("vault", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WorkingMemoryLeavesOnlyWhenTheAccessContextAllowsIt()
    {
        using var db = new SqliteTestDb();
        var (service, _, _, _) = New(db);
        MindStateSnapshot snapshot = await service.CaptureAsync(
            MindId, Components(), ConsistencyLevel.Strict, Ct);

        RedactedExport withheld = await service.ExportAsync(
            snapshot.Id, new ExportAccessContext("owner", IncludeWorkingMemory: false), Ct);

        Assert.DoesNotContain("working_memory", withheld.Sections.Keys);
        Assert.Contains(withheld.RedactedSections, s => s.Contains("short retention", StringComparison.Ordinal));
    }

    // ---- limit case: a newer body layout ----

    [Fact]
    public async Task ANewerSchemaIsNotReadPermissively()
    {
        using var db = new SqliteTestDb();
        var (service, _, _, _) = New(db);
        MindStateSnapshot snapshot = await service.CaptureAsync(
            MindId, Components(), ConsistencyLevel.Strict, Ct);

        using (var connection = db.Factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE mind_state_snapshot SET schema_version = 99;";
            command.ExecuteNonQuery();
        }

        VerificationReport report = await service.VerifyAsync(snapshot.Id, Ct);

        Assert.NotEqual(SnapshotStatus.Verified, report.Status);
        Assert.Contains("migration", report.Detail!, StringComparison.OrdinalIgnoreCase);
    }
}
