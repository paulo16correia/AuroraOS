using Aurora.Adapters.Consent;
using Aurora.Adapters.Events;
using Aurora.Adapters.Incidents;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Plugins;
using Aurora.Adapters.Plugins.Sandboxes;
using Aurora.Adapters.Tools;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// RFC 09 rule 5: a high-risk incident revokes affected capacity, preserves evidence and notifies
/// the owner.
/// </summary>
/// <remarks>
/// All three used to exist separately and none of them was reachable from a single event. What is
/// asserted here is that they happen together, in that order, and that the record afterwards says
/// what was actually achieved rather than what was attempted.
/// </remarks>
public sealed class IncidentTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly Principal Caller = new("c1", "u1");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");

    /// <summary>A prompt that remembers what it was told to say, and that opens no window.</summary>
    private sealed class RecordingPrompt : IOperatorPrompt
    {
        public List<string> Notifications { get; } = [];

        public bool IsAvailable => true;

        public Task<OperatorAnswer> AskAsync(
            string title, string question, bool secret, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(new OperatorAnswer(false, null, "not asked"));

        public Task NotifyAsync(string title, string message, CancellationToken ct)
        {
            Notifications.Add($"{title}: {message}");
            return Task.CompletedTask;
        }
    }

    private static (SqliteIncidentService Service, SqliteConsentSessionStore Sessions,
        RecordingPrompt Prompt, SqliteAuditStore Audit) Build(SqliteTestDb db)
    {
        var clock = new TestClock(Now);
        var bus = TestBus.Over(db.Factory, clock);

        var sessions = new SqliteConsentSessionStore(
            db.Factory, clock, new FakeServerIdentity("boot-1"),
            new VersionedFakePolicy(true, "pv-1"), ConsentSessionOptions.Default);

        var audit = new SqliteAuditStore(
            db.Factory, clock, new byte[32],
            new AuditAnchorFile(Path.Combine(Path.GetTempPath(), $"inc-{Guid.NewGuid():N}")));

        SqliteToolManager tools = ToolManagerTestsSupport.Manager(db, out _);

        var plugins = new SqlitePluginRegistry(
            db.Factory,
            new SubprocessPluginHost(
                Path.Combine(Path.GetTempPath(), $"inc-plug-{Guid.NewGuid():N}"),
                new UnconfinedSandbox("not used"), allowUnconfined: true),
            bus, Enumerable.Repeat((byte)9, 32).ToArray(), clock);

        var prompt = new RecordingPrompt();

        return (
            new SqliteIncidentService(db.Factory, sessions, tools, plugins, audit, bus, prompt, clock),
            sessions, prompt, audit);
    }

    private static SecurityEvent Event(
        string severity = SecuritySeverity.High,
        string resource = "",
        string evidence = "audit/42") => new(
        string.Empty, severity, SecurityEventType.SecretExposed, "corr-1", "kernel",
        resource, null, evidence, string.Empty);

    [Fact]
    public async Task AHighRiskIncidentRevokesConsentRecordsEvidenceAndNotifies()
    {
        using var db = new SqliteTestDb();
        (SqliteIncidentService service, SqliteConsentSessionStore sessions,
            RecordingPrompt prompt, SqliteAuditStore audit) = Build(db);

        // Standing permission to act without asking again — the thing that should not still be
        // true while something is going wrong.
        await sessions.OpenAsync(Caller, Ct);
        Assert.Equal(1, await sessions.CountActiveAsync(Ct));

        Incident incident = await service.OpenAsync(Event(), Ct);

        // Revoked.
        Assert.Equal(0, await sessions.CountActiveAsync(Ct));
        Assert.Contains(incident.ContainmentActions, a => a.Contains("consent session", StringComparison.Ordinal));

        // Recorded, in the one place that cannot be edited without being detected.
        IReadOnlyList<AuditRecordView> records = await audit.QueryAsync(0, 50, Ct);
        AuditRecordView entry = Assert.Single(records, r => r.ActionId == "security.incident");
        Assert.Equal("contained", entry.Outcome);
        Assert.Equal(SecuritySeverity.High, entry.Risk);
        Assert.Equal(SecurityEventType.SecretExposed, entry.Via);

        // And the owner was told, after the revocation rather than before it.
        Assert.Single(prompt.Notifications);
        Assert.Contains("contained a security incident", prompt.Notifications[0], StringComparison.Ordinal);

        Assert.Equal(IncidentStatus.Contained, incident.Status);
        Assert.NotNull(incident.ContainedAtUtc);
    }

    [Fact]
    public async Task ALowSeverityEventIsRecordedWithoutRevokingOrAlarming()
    {
        using var db = new SqliteTestDb();
        (SqliteIncidentService service, SqliteConsentSessionStore sessions,
            RecordingPrompt prompt, _) = Build(db);

        await sessions.OpenAsync(Caller, Ct);

        Incident incident = await service.OpenAsync(Event(SecuritySeverity.Low), Ct);

        // An alert per recorded event is an alert people turn off, and then the one that mattered
        // arrives silenced. Below high risk, nothing is revoked and nobody is woken.
        Assert.Equal(IncidentStatus.Open, incident.Status);
        Assert.Empty(incident.ContainmentActions);
        Assert.Empty(prompt.Notifications);
        Assert.Equal(1, await sessions.CountActiveAsync(Ct));
    }

    [Fact]
    public async Task ContainmentIsTargetedWhenTheEventNamesWhatItAffected()
    {
        using var db = new SqliteTestDb();
        (SqliteIncidentService service, _, _, _) = Build(db);
        SqliteToolManager tools = ToolManagerTestsSupport.Manager(db, out _);
        await tools.RegisterAsync(ToolManagerTestsSupport.Connector(), Ct);

        Incident incident = await service.OpenAsync(Event(resource: "tool/mailer"), Ct);

        Assert.Contains(
            incident.ContainmentActions,
            a => a.Contains("disabled tool mailer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AContainmentStepThatFailsIsRecordedAndDoesNotStopTheOthers()
    {
        using var db = new SqliteTestDb();
        (SqliteIncidentService service, _, RecordingPrompt prompt, _) = Build(db);

        // A plugin that was never installed cannot be disabled. The point is what happens next:
        // the consent revocation before it still counted, and the owner is still told.
        Incident incident = await service.OpenAsync(Event(resource: "plugin/nothing"), Ct);

        Assert.Equal(2, incident.ContainmentActions.Count);
        Assert.Contains(
            incident.ContainmentActions,
            a => a.Contains("not installed", StringComparison.Ordinal));

        Assert.Single(prompt.Notifications);
        Assert.Equal(IncidentStatus.Contained, incident.Status);
    }

    [Fact]
    public async Task AnEventWithNoEvidenceIsNotAnIncident()
    {
        using var db = new SqliteTestDb();
        (SqliteIncidentService service, _, _, _) = Build(db);

        // Rule 5 asks for evidence to be preserved. One that cites none preserves nothing, and
        // leaves whoever reads it with an assertion and no way to check it.
        await Assert.ThrowsAsync<IncidentException>(
            () => service.OpenAsync(Event(evidence: "  "), Ct));
    }

    [Fact]
    public async Task AnIncidentIsResolvedWithAReasonAndStaysOnTheRecord()
    {
        using var db = new SqliteTestDb();
        (SqliteIncidentService service, _, _, SqliteAuditStore audit) = Build(db);

        Incident opened = await service.OpenAsync(Event(), Ct);
        Assert.Single(await service.OpenIncidentsAsync(Ct));

        await Assert.ThrowsAsync<IncidentException>(
            () => service.ResolveAsync(opened.Id, "  ", "owner", Ct));

        Incident resolved = await service.ResolveAsync(
            opened.Id, "the key was rotated", "owner", Ct);

        Assert.Equal(IncidentStatus.Resolved, resolved.Status);
        Assert.Empty(await service.OpenIncidentsAsync(Ct));

        // Resolved is not deleted: a system whose incident log empties itself cannot show it has
        // ever been attacked.
        Incident? still = await service.GetAsync(opened.Id, Ct);
        Assert.Equal("the key was rotated", still!.Resolution);

        Assert.Contains(
            await audit.QueryAsync(0, 50, Ct),
            r => r.ActionId == "security.incident.resolve");

        await Assert.ThrowsAsync<IncidentException>(
            () => service.ResolveAsync(opened.Id, "again", "owner", Ct));
    }

    [Fact]
    public async Task ABrokenAuditChainAndAWrongClockAreFoundByTheUpkeepPass()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(Now);
        var bus = TestBus.Over(db.Factory, clock);
        var incidents = new RecordingIncidentService();
        var cycles = new Adapters.Cognition.SqliteCognitiveCycle(db.Factory, clock);
        var signals = new Adapters.Signals.SqliteSignalService(db.Factory, cycles, clock);

        var needs = new Adapters.Needs.SqliteNeedsService(
            db.Factory, new Adapters.Planning.SqlitePlanner(db.Factory, clock, bus), clock);

        var resources = new Adapters.Resources.SystemResourceModel(new FakeResourceProbe(), clock);

        // An audit store whose chain does not verify, and a clock that went backwards.
        var broken = new BrokenChainAuditStore();

        var maintenance = new Adapters.Maintenance.MaintenanceService(
            new Adapters.Scheduling.SqliteScheduler(db.Factory, bus, cycles, clock),
            signals, needs,
            new Adapters.Situation.SituationService(signals, needs, resources, QuietHours.Default, clock),
            resources, new InMemoryIdempotencyStore(), new FakeApprovalStore(),
            new Adapters.Retention.SqliteRetentionService(db.Factory, clock), RetentionPolicy.Default,
            bus, broken, new FakeClockGuard(trustworthy: false),
            incidents, new NoOperatorPrompt(), clock);

        await maintenance.RunAsync(new SituationContext("Europe/Lisbon"), Ct);

        // Both were already detectable and neither revoked anything: a broken chain was a health
        // check that read FAIL, and a backwards clock was a verdict nobody acted on.
        Assert.Equal(
            [SecurityEventType.AuditChainBroken, SecurityEventType.ClockTampering],
            incidents.Opened.Select(e => e.Type));

        // CRITICAL for the chain: every other guarantee Aurora offers is checked against that log.
        Assert.Equal(SecuritySeverity.Critical, incidents.Opened[0].Severity);
        Assert.Contains("audit/", incidents.Opened[0].EvidenceRef, StringComparison.Ordinal);
    }

    /// <summary>An audit store that reports a break at a known sequence.</summary>
    private sealed class BrokenChainAuditStore : IAuditStore
    {
        public Task<string> AppendAsync(AuditEntry entry, CancellationToken ct) =>
            Task.FromResult("audit-1");

        public Task<AuditVerification> VerifyChainAsync(CancellationToken ct) =>
            Task.FromResult(new AuditVerification(false, 42, "hash mismatch"));

        public Task<string> SealBreakAsync(string reason, string actor, CancellationToken ct) =>
            Task.FromResult("seal-1");

        public Task<IReadOnlyList<AuditRecordView>> QueryAsync(
            long afterSequence, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AuditRecordView>>([]);

        public Task<string?> HeadHashAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
