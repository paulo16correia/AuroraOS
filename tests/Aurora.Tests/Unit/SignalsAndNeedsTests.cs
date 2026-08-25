using System.Globalization;
using Aurora.Adapters.Cognition;
using Aurora.Adapters.Events;
using Aurora.Adapters.Needs;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Planning;
using Aurora.Adapters.Signals;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Signals (RFC 030) and Needs (RFC 031): priority without permission.
/// </summary>
/// <remarks>
/// The property both share, and the one worth testing hardest, is what they cannot do. Urgency
/// changes what Aurora looks at and in what order; it never changes what Aurora is allowed to do.
/// </remarks>
public sealed class SignalsAndNeedsTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record World(
        SqliteSignalService Signals,
        SqliteNeedsService Needs,
        SqliteEventBus Bus,
        SqliteCognitiveCycle Cycles,
        SqlitePlanner Planner,
        TestClock Clock);

    private static World Build(SqliteTestDb db, string now = "2026-01-15T09:00:00+00:00")
    {
        var clock = new TestClock(At(now));
        var cycles = new SqliteCognitiveCycle(db.Factory, clock);
        var planner = new SqlitePlanner(db.Factory, clock, TestBus.Over(db.Factory, clock));

        return new World(
            new SqliteSignalService(db.Factory, cycles, clock),
            new SqliteNeedsService(db.Factory, planner, clock),
            new SqliteEventBus(db.Factory, new SqliteOutbox(new PermissiveEventCatalogue(), clock), clock),
            cycles, planner, clock);
    }

    private static SignalClassification Alert(
        string severity = SignalSeverity.High, string target = "host/local") =>
        new(SignalKind.Alert, severity, Urgency: 0.9, Relevance: 0.8, Confidence: 0.9,
            TargetRefs: [target], Lifetime: TimeSpan.FromHours(1));

    private static Task<DomainEvent> FactAsync(World world) =>
        world.Bus.PublishAsync(
            new OutboxWrite(
                "DiskFilling", 1, "health", "c-1", Sensitivity.Private,
                PayloadJson: """{"free_pct":4}"""),
            Ct);

    // ---- RFC 030 rule 1: a signal needs a source that actually happened ----

    [Fact]
    public async Task ASignalCannotBeRaisedAboutSomethingThatDidNotHappen()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        // Without this check, a classifier could invent the urgency and the evidence for it in the
        // same breath, and nothing downstream could tell the difference.
        await Assert.ThrowsAsync<SignalException>(() =>
            world.Signals.EmitAsync("event/never-happened", Alert(), SignalPolicy.Default, Ct));
    }

    [Fact]
    public async Task ASignalAboutACommittedEventIsAccepted()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        DomainEvent fact = await FactAsync(world);

        Signal signal = await world.Signals.EmitAsync(fact.EventId, Alert(), SignalPolicy.Default, Ct);

        Assert.Equal(SignalStatus.New, signal.Status);
        Assert.Equal(fact.EventId, signal.SourceEventRef);
    }

    // ---- RFC 030 rule 4: signals end ----

    [Fact]
    public async Task ASignalWithNoLifetimeIsRefused()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        DomainEvent fact = await FactAsync(world);

        // A signal that never expires is a permanent claim on attention, which is the one thing a
        // signal must not become.
        await Assert.ThrowsAsync<SignalException>(() =>
            world.Signals.EmitAsync(
                fact.EventId, Alert() with { Lifetime = TimeSpan.Zero }, SignalPolicy.Default, Ct));
    }

    [Fact]
    public async Task AnExpiredSignalStopsBeingPendingAndIsNotRouted()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        DomainEvent fact = await FactAsync(world);

        Signal signal = await world.Signals.EmitAsync(
            fact.EventId, Alert() with { Lifetime = TimeSpan.FromMinutes(5) }, SignalPolicy.Default, Ct);

        world.Clock.UtcNow = At("2026-01-15T10:00:00+00:00");

        Assert.Equal(1, await world.Signals.ExpireDueAsync(Ct));
        Assert.Empty(await world.Signals.PendingAsync(Ct));

        RouteDecision route = await world.Signals.RouteAsync(signal.Id, null, SignalPolicy.Default, Ct);
        Assert.Contains(SignalReason.Expired, route.ReasonCodes);
    }

    // ---- RFC 030 rule 3: interrupting takes a threshold, and preserves what it interrupted ----

    [Fact]
    public async Task AnInterruptingSignalParksTheCycleItInterruptedRatherThanEndingIt()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        DomainEvent fact = await FactAsync(world);

        Signal signal = await world.Signals.EmitAsync(
            fact.EventId, Alert(SignalSeverity.Critical), SignalPolicy.Default, Ct);

        CognitiveCycle inProgress = await world.Cycles.RunAsync(
            new CycleIngress("work", "c-2", null), Ct);

        RouteDecision route = await world.Signals.RouteAsync(
            signal.Id, inProgress.Id, SignalPolicy.Default, Ct);

        Assert.Equal(Interruptibility.Emergency, route.Interruptibility);
        Assert.Equal(inProgress.Id, route.PreservedCycleId);

        // Parked, not cancelled: an urgent alert that destroyed the work it interrupted would cost
        // more than the thing it interrupted for.
        CognitiveCycle? preserved = await world.Cycles.GetAsync(inProgress.Id, Ct);
        Assert.Equal(CycleStatus.Waiting, preserved!.Status);

        CognitiveCycle resumed = await world.Cycles.ResumeAsync(inProgress.Id, "signal handled", Ct);
        Assert.Equal(CycleStatus.Running, resumed.Status);
    }

    [Fact]
    public async Task ASignalBelowTheThresholdWaitsItsTurnInsteadOfInterrupting()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        DomainEvent fact = await FactAsync(world);

        Signal signal = await world.Signals.EmitAsync(
            fact.EventId, Alert(SignalSeverity.Low), SignalPolicy.Default, Ct);

        CognitiveCycle inProgress = await world.Cycles.RunAsync(
            new CycleIngress("work", "c-3", null), Ct);

        RouteDecision route = await world.Signals.RouteAsync(
            signal.Id, inProgress.Id, SignalPolicy.Default, Ct);

        Assert.Equal(Interruptibility.Queue, route.Interruptibility);
        Assert.Contains(SignalReason.BelowInterruptThreshold, route.ReasonCodes);
        Assert.Null(route.PreservedCycleId);

        CognitiveCycle? untouched = await world.Cycles.GetAsync(inProgress.Id, Ct);
        Assert.Equal(CycleStatus.Running, untouched!.Status);
    }

    [Fact]
    public async Task TheInterruptThresholdIsPolicyRatherThanAPropertyOfTheSignal()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        DomainEvent fact = await FactAsync(world);

        Signal signal = await world.Signals.EmitAsync(
            fact.EventId, Alert(SignalSeverity.Medium), SignalPolicy.Default, Ct);

        CognitiveCycle inProgress = await world.Cycles.RunAsync(
            new CycleIngress("work", "c-4", null), Ct);

        // The same signal, judged against a policy that says MEDIUM is worth stopping for.
        RouteDecision route = await world.Signals.RouteAsync(
            signal.Id, inProgress.Id,
            SignalPolicy.Default with { InterruptAtSeverity = SignalSeverity.Medium }, Ct);

        Assert.Equal(Interruptibility.Interrupt, route.Interruptibility);
    }

    // ---- RFC 030 limit case: a signal storm ----

    [Fact]
    public async Task RepeatsOfTheSameSignalAreHeldBackAndSayWhy()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        DomainEvent fact = await FactAsync(world);

        Signal first = await world.Signals.EmitAsync(fact.EventId, Alert(), SignalPolicy.Default, Ct);
        Signal second = await world.Signals.EmitAsync(fact.EventId, Alert(), SignalPolicy.Default, Ct);

        Assert.Equal(SignalStatus.New, first.Status);

        // Suppressed and recorded, not dropped: a signal nobody can find afterwards is
        // indistinguishable from one that was never raised.
        Assert.Equal(SignalStatus.Suppressed, second.Status);
        Assert.Contains(SignalReason.Duplicate, second.ReasonCodes);
        Assert.NotNull(await world.Signals.GetAsync(second.Id, Ct));
    }

    // ---- RFC 031 rule 1: a need carries its evidence and how it ends ----

    [Fact]
    public async Task EveryDetectedNeedStatesItsEvidenceAndWhatWouldSatisfyIt()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        IReadOnlyList<Need> needs = await world.Needs.DetectAsync(
            new NeedsSnapshot(DeadLetters: 3, PendingApprovals: 1, OverdueGoals: 2), [], Ct);

        Assert.NotEmpty(needs);
        Assert.All(needs, need =>
        {
            Assert.NotEmpty(need.EvidenceRefs);
            Assert.False(string.IsNullOrWhiteSpace(need.SatisfactionCondition));
        });
    }

    [Fact]
    public async Task ANeedCannotBeDeclaredMetWithoutTheEvidenceThatMetIt()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        Need need = (await world.Needs.DetectAsync(new NeedsSnapshot(DeadLetters: 1), [], Ct)).Single();

        await Assert.ThrowsAsync<NeedException>(() => world.Needs.SatisfyAsync(need.Id, "", Ct));

        Need satisfied = await world.Needs.SatisfyAsync(need.Id, "delivery/drained", Ct);
        Assert.Equal(NeedStatus.Satisfied, satisfied.Status);
    }

    [Fact]
    public async Task TheSameConditionSeenTwiceIsOneNeedRatherThanTwo()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        Need first = (await world.Needs.DetectAsync(new NeedsSnapshot(DeadLetters: 1), [], Ct)).Single();
        Need again = (await world.Needs.DetectAsync(new NeedsSnapshot(DeadLetters: 4), [], Ct)).Single();

        // The list of needs describes how things stand, not every time Aurora noticed.
        Assert.Equal(first.Id, again.Id);
        Assert.True(again.Intensity > first.Intensity);
    }

    // ---- RFC 031 rule 2: a need drafts, and that is all it does ----

    [Fact]
    public async Task ANeedCanOnlyDraftAGoalAndNeverStartWork()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        Need need = (await world.Needs.DetectAsync(new NeedsSnapshot(DeadLetters: 2), [], Ct)).Single();
        Need planned = await world.Needs.PlanAsync(need.Id, Ct);

        Assert.Equal(NeedStatus.Planned, planned.Status);
        Assert.NotNull(planned.RecommendedGoalRef);

        Goal? goal = await world.Planner.GetGoalAsync(planned.RecommendedGoalRef!, Ct);

        // DRAFT: a condition Aurora noticed is a reason to consider something, not a decision to
        // do it. There is no plan and no task attached.
        Assert.Equal(GoalStatus.Draft, goal!.Status);
        Assert.Null(await world.Planner.GetActivePlanAsync(goal.Id, Ct));
    }

    // ---- RFC 031 rule 3: what the person asked for outranks housekeeping ----

    [Fact]
    public async Task MaintenanceWaitsBehindThePersonAndBothWaitBehindAnIncident()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        await world.Needs.DetectAsync(
            new NeedsSnapshot(
                DeadLetters: 9, PendingApprovals: 1, UnreconciledReservations: 1), [], Ct);

        IReadOnlyList<Need> ranked = await world.Needs.RankAsync(Ct);

        // Recovery first — the system failing to keep its own promises. Then the person. Then the
        // work that can always wait one more hour, no matter how much of it has piled up.
        Assert.Equal(NeedKind.Recovery, ranked[0].Kind);
        Assert.Equal(NeedOwner.User, ranked[1].Owner);
        Assert.Equal(NeedKind.Maintenance, ranked[^1].Kind);
    }

    [Fact]
    public async Task AnIncidentIsNotSomethingAuroraCanPutOff()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        Need recovery = (await world.Needs
            .DetectAsync(new NeedsSnapshot(UnreconciledReservations: 2), [], Ct)).Single();

        await Assert.ThrowsAsync<NeedException>(() =>
            world.Needs.DeferAsync(recovery.Id, At("2026-02-01T00:00:00+00:00"), "later", Ct));
    }

    [Fact]
    public async Task ADeferredNeedDropsOutOfTheRankingUntilItsTime()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        Need chore = (await world.Needs.DetectAsync(new NeedsSnapshot(DeadLetters: 1), [], Ct)).Single();
        await world.Needs.DeferAsync(chore.Id, At("2026-01-16T09:00:00+00:00"), "outside the window", Ct);

        Assert.Empty(await world.Needs.RankAsync(Ct));

        world.Clock.UtcNow = At("2026-01-16T10:00:00+00:00");
        Assert.Single(await world.Needs.RankAsync(Ct));
    }

    // ---- RFC 031 rule 4: no eternal urgencies ----

    [Fact]
    public async Task ANeedNobodyActsOnGetsQuieterRatherThanLouder()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        Need need = (await world.Needs.DetectAsync(new NeedsSnapshot(DeadLetters: 10), [], Ct)).Single();
        Assert.Equal(1.0, need.Intensity, 3);

        world.Clock.UtcNow = At("2026-01-16T09:00:00+00:00");
        await world.Needs.DecayAsync(Ct);

        Need quieter = (await world.Needs.GetAsync(need.Id, Ct))!;

        // A day of nobody acting is evidence that it was less urgent than it claimed. Halving it
        // is what keeps the loud ones meaningful.
        Assert.Equal(0.5, quieter.Intensity, 2);
    }

    // ---- the join: a severe signal becomes something that outlives it ----

    [Fact]
    public async Task ASevereSignalBecomesANeedSoItStillMattersAfterTheSignalExpires()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        DomainEvent fact = await FactAsync(world);

        Signal signal = await world.Signals.EmitAsync(
            fact.EventId, Alert(SignalSeverity.Critical), SignalPolicy.Default, Ct);

        Need need = Assert.Single(
            await world.Needs.DetectAsync(new NeedsSnapshot(), [signal], Ct));

        Assert.Equal(NeedKind.Safety, need.Kind);
        Assert.Contains(signal.Id, need.EvidenceRefs);
        Assert.Contains(signal.Id, need.SatisfactionCondition);
    }
}
