using System.Globalization;
using Aurora.Adapters.Cognition;
using Aurora.Adapters.Events;
using Aurora.Adapters.Maintenance;
using Aurora.Adapters.Needs;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Planning;
using Aurora.Adapters.Resources;
using Aurora.Adapters.Retention;
using Aurora.Adapters.Scheduling;
using Aurora.Adapters.Signals;
using Aurora.Adapters.Situation;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Resources (RFC 033), situational awareness (RFC 034) and the upkeep pass.
/// </summary>
/// <remarks>
/// The shared property: none of these grants anything. They decide whether there is room and
/// whether it is a good moment, which can only ever make Aurora quieter or more careful.
/// </remarks>
public sealed class StatusAndMaintenanceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Lisbon = "Europe/Lisbon";

    private static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    // ---- RFC 033: capacity, and what an unreadable machine means ----

    [Fact]
    public async Task AMetricThisPlatformCannotReportIsNamedRatherThanReportedAsZero()
    {
        var model = new SystemResourceModel(new FakeResourceProbe(), new TestClock(At("2026-01-15T09:00:00+00:00")));
        ResourceState state = await model.ObserveAsync(Ct);

        // There is no portable reading of connectivity that means anything useful, so it says so.
        // Treating an unread metric as a healthy one is how a system becomes least reliable exactly
        // when there is most going on.
        Assert.Contains("network", state.Unmeasured);
        Assert.Equal(NetworkState.Unknown, state.NetworkState);
    }

    [Fact]
    public async Task AHostThatReportsNothingIsUnknownRatherThanHealthy()
    {
        var model = new SystemResourceModel(
            FakeResourceProbe.Blind(), new TestClock(At("2026-01-15T09:00:00+00:00")));

        ResourceState state = await model.ObserveAsync(Ct);

        Assert.Equal(ResourceStatus.Unknown, state.Status);
        Assert.Contains("cpu", state.Unmeasured);

        // And admission gets conservative: optional work waits until something can be measured.
        AdmissionResult optional = await model.AdmitAsync(
            "index/1", WorkClass.Discretionary, 0.1, ResourceBudget.Default, Ct);

        Assert.Equal(Admission.Defer, optional.Decision);
    }

    [Fact]
    public async Task UnderRealPressureHousekeepingGivesWayBeforeAnythingElse()
    {
        var probe = new FakeResourceProbe(cpu: 0.85);
        var model = new SystemResourceModel(probe, new TestClock(At("2026-01-15T09:00:00+00:00")));

        // Constrained: curiosity, indexing and consolidation are what go first (rule 1).
        Assert.Equal(
            Admission.Defer,
            (await model.AdmitAsync("index/1", WorkClass.Discretionary, 0.1, ResourceBudget.Default, Ct)).Decision);

        Assert.Equal(
            Admission.Allow,
            (await model.AdmitAsync("task/1", WorkClass.Ordinary, 0.1, ResourceBudget.Default, Ct)).Decision);

        // Critical: only what the system's integrity or the person is waiting on proceeds (rule 3).
        probe.Cpu = 0.97;

        Assert.Equal(
            Admission.Defer,
            (await model.AdmitAsync("task/2", WorkClass.Ordinary, 0.1, ResourceBudget.Default, Ct)).Decision);

        Assert.Equal(
            Admission.Allow,
            (await model.AdmitAsync("recovery/1", WorkClass.Essential, 0.1, ResourceBudget.Default, Ct)).Decision);
    }

    [Fact]
    public async Task WorkThatWillNotSayWhatItCostsIsNotAdmitted()
    {
        var model = new SystemResourceModel(new FakeResourceProbe(), new TestClock(At("2026-01-15T09:00:00+00:00")));

        AdmissionResult refused = await model.AdmitAsync(
            "task/1", WorkClass.Ordinary, double.PositiveInfinity, ResourceBudget.Default, Ct);

        Assert.Equal(Admission.Deny, refused.Decision);
    }

    [Fact]
    public async Task HousekeepingCannotFillTheSlotsHeldForEssentialWork()
    {
        var model = new SystemResourceModel(new FakeResourceProbe(), new TestClock(At("2026-01-15T09:00:00+00:00")));
        var budget = ResourceBudget.Default with { MaxConcurrency = 2, ReserveForCritical = 1 };

        AdmissionResult first = await model.AdmitAsync("chore/1", WorkClass.Discretionary, 0.1, budget, Ct);
        AdmissionResult second = await model.AdmitAsync("chore/2", WorkClass.Discretionary, 0.1, budget, Ct);

        Assert.Equal(Admission.Allow, first.Decision);

        // The reserve is what stops maintenance from filling every slot and leaving nothing for the
        // work that cannot wait.
        Assert.Equal(Admission.Defer, second.Decision);

        AdmissionResult essential = await model.AdmitAsync("recovery/1", WorkClass.Essential, 0.1, budget, Ct);
        Assert.Equal(Admission.Allow, essential.Decision);
    }

    [Fact]
    public async Task AnExhaustedBudgetDefersRatherThanBillingOnward()
    {
        var model = new SystemResourceModel(new FakeResourceProbe(), new TestClock(At("2026-01-15T09:00:00+00:00")));
        var budget = ResourceBudget.Default with { MaxCost = 1.0 };

        await model.AdmitAsync("task/1", WorkClass.Ordinary, 0.9, budget, Ct);
        AdmissionResult over = await model.AdmitAsync("task/2", WorkClass.Ordinary, 0.9, budget, Ct);

        // Deferred, not denied: the work is fine, the moment is not, and a cheaper option or a
        // later window is still open.
        Assert.Equal(Admission.Defer, over.Decision);
        Assert.Contains("budget", over.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReleasingGivesTheCapacityBack()
    {
        var model = new SystemResourceModel(new FakeResourceProbe(), new TestClock(At("2026-01-15T09:00:00+00:00")));
        var budget = ResourceBudget.Default with { MaxConcurrency = 1, ReserveForCritical = 0 };

        AdmissionResult held = await model.AdmitAsync("task/1", WorkClass.Ordinary, 0.1, budget, Ct);
        Assert.Equal(Admission.Defer, (await model.AdmitAsync("task/2", WorkClass.Ordinary, 0.1, budget, Ct)).Decision);

        await model.ReleaseAsync(held.ReservationId!, "completed", Ct);

        Assert.Equal(Admission.Allow, (await model.AdmitAsync("task/2", WorkClass.Ordinary, 0.1, budget, Ct)).Decision);
        Assert.Single(model.Held);
    }

    // ---- RFC 034: reading the moment ----

    private sealed record World(
        SituationService Situation, SqliteSignalService Signals, SqliteNeedsService Needs,
        SystemResourceModel Resources, SqliteEventBus Bus, TestClock Clock);

    private static World Build(SqliteTestDb db, string now, QuietHours? quiet = null)
    {
        var clock = new TestClock(At(now));
        var cycles = new SqliteCognitiveCycle(db.Factory, clock);
        var signals = new SqliteSignalService(db.Factory, cycles, clock);
        var needs = new SqliteNeedsService(db.Factory, new SqlitePlanner(db.Factory, clock), clock);
        var resources = new SystemResourceModel(new FakeResourceProbe(), clock);

        return new World(
            new SituationService(signals, needs, resources, quiet ?? QuietHours.Default, clock),
            signals, needs, resources,
            new SqliteEventBus(db.Factory, new SqliteOutbox(new PermissiveEventCatalogue(), clock), clock), clock);
    }

    private static Task<DomainEvent> FactAsync(World world) =>
        world.Bus.PublishAsync(
            new OutboxWrite(
                "Observed", 1, "health", "c-1", Sensitivity.Private, PayloadJson: "{}"), Ct);

    [Fact]
    public async Task ReceivedContentCannotDeclareAnEmergency()
    {
        using var db = new SqliteTestDb();
        World world = Build(db, "2026-01-15T14:00:00+00:00");
        DomainEvent fact = await FactAsync(world);

        // A message that says URGENT is still a message. RFC 034 rule 4: external content does not
        // get to raise Aurora's posture to a crisis on the sender's say-so.
        await world.Signals.EmitAsync(
            fact.EventId,
            new SignalClassification(
                SignalKind.Message, SignalSeverity.Critical, 1.0, 1.0, 1.0, ["inbox/1"],
                TimeSpan.FromHours(1)),
            SignalPolicy.Default, Ct);

        SituationAssessment assessment = await world.Situation.AssessAsync(
            new SituationContext(Lisbon, UserAvailability: UserAvailability.Online), Ct);

        Assert.Equal(RiskPosture.Elevated, assessment.RiskPosture);
        Assert.NotEqual(RiskPosture.Emergency, assessment.RiskPosture);
    }

    [Fact]
    public async Task AuroraSOwnHealthObservationCanDeclareAnEmergency()
    {
        using var db = new SqliteTestDb();
        World world = Build(db, "2026-01-15T14:00:00+00:00");
        DomainEvent fact = await FactAsync(world);

        await world.Signals.EmitAsync(
            fact.EventId,
            new SignalClassification(
                SignalKind.Health, SignalSeverity.Critical, 1.0, 1.0, 1.0, ["host/local"],
                TimeSpan.FromHours(1)),
            SignalPolicy.Default, Ct);

        SituationAssessment assessment = await world.Situation.AssessAsync(
            new SituationContext(Lisbon, UserAvailability: UserAvailability.Online), Ct);

        Assert.Equal(RiskPosture.Emergency, assessment.RiskPosture);
        Assert.Equal(ResponseMode.EssentialOnly, assessment.RecommendedResponseMode);
    }

    [Fact]
    public async Task BeingOfflineIsNotConsent()
    {
        using var db = new SqliteTestDb();
        World world = Build(db, "2026-01-15T14:00:00+00:00");

        SituationAssessment assessment = await world.Situation.AssessAsync(
            new SituationContext(Lisbon, UserAvailability: UserAvailability.Offline), Ct);

        // Rule 2: not being there changes when and how the person is reached, and never whether
        // Aurora may go ahead without them.
        AppropriatenessResult imposing = world.Situation.IsAppropriate(
            WorkClass.Ordinary, imposesOnUser: true, assessment);

        Assert.False(imposing.Appropriate);

        // Internal work is unaffected, which is the whole distinction.
        Assert.True(world.Situation.IsAppropriate(
            WorkClass.Ordinary, imposesOnUser: false, assessment).Appropriate);
    }

    [Fact]
    public async Task QuietHoursStopAuroraReachingOutWithoutStoppingItWorking()
    {
        using var db = new SqliteTestDb();

        // 23:00 in Lisbon in January is 23:00 UTC.
        World world = Build(db, "2026-01-15T23:00:00+00:00");

        SituationAssessment assessment = await world.Situation.AssessAsync(
            new SituationContext(Lisbon, UserAvailability: UserAvailability.Online), Ct);

        Assert.True(assessment.QuietHoursActive);
        Assert.Equal(ResponseMode.SilentInternalWork, assessment.RecommendedResponseMode);

        Assert.False(world.Situation.IsAppropriate(WorkClass.Ordinary, true, assessment).Appropriate);
        Assert.True(world.Situation.IsAppropriate(WorkClass.Ordinary, false, assessment).Appropriate);

        // And an emergency still gets through, because that is what essential means.
        Assert.True(world.Situation.IsAppropriate(WorkClass.Essential, true, assessment).Appropriate);
    }

    [Fact]
    public async Task AStaleReadingOfTheRoomIsRefusedRatherThanReused()
    {
        using var db = new SqliteTestDb();
        World world = Build(db, "2026-01-15T14:00:00+00:00");

        SituationAssessment assessment = await world.Situation.AssessAsync(
            new SituationContext(Lisbon, UserAvailability: UserAvailability.Online), Ct);

        Assert.True(world.Situation.IsAppropriate(WorkClass.Ordinary, false, assessment).Appropriate);

        world.Clock.UtcNow = At("2026-01-15T15:00:00+00:00");

        // Rule 1: conditions move. Yesterday's sense of the room is worse than admitting there is
        // no current one.
        AppropriatenessResult stale = world.Situation.IsAppropriate(WorkClass.Ordinary, false, assessment);

        Assert.False(stale.Appropriate);
        Assert.Contains("expired", stale.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnUnknownTimeZoneIsRefusedRatherThanAssumed()
    {
        using var db = new SqliteTestDb();
        World world = Build(db, "2026-01-15T14:00:00+00:00");

        await Assert.ThrowsAsync<SituationException>(() =>
            world.Situation.AssessAsync(new SituationContext("Mars/Olympus_Mons"), Ct));
    }

    // ---- the upkeep pass ----

    [Fact]
    public async Task MaintenanceReportsWhatItDidNotLookAtRatherThanCallingItZero()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T14:00:00+00:00"));
        var cycles = new SqliteCognitiveCycle(db.Factory, clock);
        var bus = new SqliteEventBus(db.Factory, new SqliteOutbox(new PermissiveEventCatalogue(), clock), clock);
        var signals = new SqliteSignalService(db.Factory, cycles, clock);
        var needs = new SqliteNeedsService(db.Factory, new SqlitePlanner(db.Factory, clock), clock);
        var resources = new SystemResourceModel(new FakeResourceProbe(), clock);

        var maintenance = new MaintenanceService(
            new SqliteScheduler(db.Factory, bus, cycles, clock),
            signals, needs,
            new SituationService(signals, needs, resources, QuietHours.Default, clock),
            resources,
            new InMemoryIdempotencyStore(),
            new FakeApprovalStore(),
            new SqliteRetentionService(db.Factory, clock), RetentionPolicy.Default,
            bus, clock);

        MaintenanceReport report = await maintenance.RunAsync(new SituationContext(Lisbon), Ct);

        // Overdue goals have no read-only count yet, so the pass says so instead of reporting none.
        Assert.Contains("overdue_goals", report.Unmeasured);
        Assert.Contains("since_last_backup", report.Unmeasured);
        Assert.DoesNotContain("dead_letters", report.Unmeasured);
    }

    [Fact]
    public async Task MaintenanceSurfacesDueWorkAndRunsNoneOfIt()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T08:00:00+00:00"));
        var cycles = new SqliteCognitiveCycle(db.Factory, clock);
        var bus = new SqliteEventBus(db.Factory, new SqliteOutbox(new PermissiveEventCatalogue(), clock), clock);
        var signals = new SqliteSignalService(db.Factory, cycles, clock);
        var needs = new SqliteNeedsService(db.Factory, new SqlitePlanner(db.Factory, clock), clock);
        var resources = new SystemResourceModel(new FakeResourceProbe(), clock);
        var scheduler = new SqliteScheduler(db.Factory, bus, cycles, clock);

        Schedule schedule = await scheduler.CreateAsync(
            new ScheduleRequest(
                "morning review", "paulo", ScheduleTrigger.Cron, Lisbon, "0 9 * * *",
                ScheduleTarget.CycleTemplate),
            null, Ct);

        var maintenance = new MaintenanceService(
            scheduler, signals, needs,
            new SituationService(signals, needs, resources, QuietHours.Default, clock),
            resources, new InMemoryIdempotencyStore(), new FakeApprovalStore(),
            new SqliteRetentionService(db.Factory, clock), RetentionPolicy.Default, bus, clock);

        clock.UtcNow = At("2026-01-15T09:00:00+00:00");
        MaintenanceReport report = await maintenance.RunAsync(new SituationContext(Lisbon), Ct);

        var runId = Assert.Single(report.DueRunIds);

        // Surfaced, and left alone. An upkeep loop that could act on what it found would be the
        // widest bypass in the system, because it runs unattended.
        ScheduleRun run = (await scheduler.RunsAsync(schedule.Id, Ct)).Single(r => r.Id == runId);
        Assert.Equal(ScheduleRunStatus.Due, run.Status);
        Assert.Null(run.CycleId);
    }

    [Fact]
    public async Task MaintenanceNoticesWhatIsWaitingWithoutPlanningItOnItsOwn()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T14:00:00+00:00"));
        var cycles = new SqliteCognitiveCycle(db.Factory, clock);
        var bus = new SqliteEventBus(db.Factory, new SqliteOutbox(new PermissiveEventCatalogue(), clock), clock);
        var signals = new SqliteSignalService(db.Factory, cycles, clock);
        var planner = new SqlitePlanner(db.Factory, clock);
        var needs = new SqliteNeedsService(db.Factory, planner, clock);
        var resources = new SystemResourceModel(new FakeResourceProbe(), clock);

        var maintenance = new MaintenanceService(
            new SqliteScheduler(db.Factory, bus, cycles, clock),
            signals, needs,
            new SituationService(signals, needs, resources, QuietHours.Default, clock),
            resources, new InMemoryIdempotencyStore(), new FakeApprovalStore(pending: 2),
            new SqliteRetentionService(db.Factory, clock), RetentionPolicy.Default, bus, clock);

        MaintenanceReport report = await maintenance.RunAsync(new SituationContext(Lisbon), Ct);

        var needId = Assert.Single(report.RankedNeedIds);
        Need noticed = (await needs.GetAsync(needId, Ct))!;

        Assert.Equal(NeedKind.Communication, noticed.Kind);

        // DETECTED, not PLANNED. Noticing is the whole of what upkeep is allowed to do; drafting a
        // goal is a separate act, and running one is several more.
        Assert.Equal(NeedStatus.Detected, noticed.Status);
        Assert.Null(noticed.RecommendedGoalRef);
    }
}
