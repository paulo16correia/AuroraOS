using System.Globalization;
using Aurora.Adapters.Applications;
using Aurora.Adapters.Cognition;
using Aurora.Adapters.Constitution;
using Aurora.Adapters.Curiosity;
using Aurora.Adapters.Events;
using Aurora.Adapters.Memories;
using Aurora.Adapters.Missions;
using Aurora.Adapters.Needs;
using Aurora.Adapters.Observations;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Planning;
using Aurora.Adapters.Resources;
using Aurora.Adapters.Scheduling;
using Aurora.Adapters.Signals;
using Aurora.Adapters.Situation;
using Aurora.Adapters.World;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// The second application (RFC 100 step 12): a low-risk reading review, through the full cycle.
/// </summary>
public sealed class ReviewApplicationTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Lisbon = "Europe/Lisbon";
    private static readonly Principal Caller = new("local-mcp-client", "paulo");

    private static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record World(
        DailyReviewApplication Review, SqliteCognitiveCycle Cycles, SqliteAuditStore Audit,
        SqliteNeedsService Needs, SqliteScheduler Scheduler, SqliteMissionService Missions,
        SqlitePlanner Planner, TestClock Clock);

    private static World Build(SqliteTestDb db, string now = "2026-01-15T14:00:00+00:00")
    {
        var clock = new TestClock(At(now));
        var anchorPath = Path.Combine(Path.GetTempPath(), $"aurora-anchor-{Guid.NewGuid():N}");

        var audit = new SqliteAuditStore(db.Factory, clock, new byte[32], new AuditAnchorFile(anchorPath));
        var bus = new SqliteEventBus(db.Factory, new SqliteOutbox(new PermissiveEventCatalogue(), clock), clock);
        var cycles = new SqliteCognitiveCycle(db.Factory, clock);
        var planner = new SqlitePlanner(db.Factory, clock, TestBus.Over(db.Factory, clock));
        var needs = new SqliteNeedsService(db.Factory, planner, clock);
        var signals = new SqliteSignalService(db.Factory, cycles, clock);
        var scheduler = new SqliteScheduler(db.Factory, bus, cycles, clock);
        var missions = new SqliteMissionService(db.Factory, planner, TestBus.Over(db.Factory, clock), clock);
        var resources = new SystemResourceModel(new FakeResourceProbe(), clock);
        var situation = new SituationService(signals, needs, resources, QuietHours.Default, clock);
        var curiosity = new SqliteCuriosityEngine(db.Factory, planner, resources, situation, needs, clock);

        var review = new DailyReviewApplication(
            cycles, bus,
            new SqliteAttentionSystem(db.Factory, new SensitivityAttentionAuthorization(), clock),
            new SqliteWorkingMemory(db.Factory, clock, WorkingMemoryOptions.Default),
            new SqliteWorldModel(db.Factory, clock, WorldModelOptions.Default),
            new SqliteDecisionEngine(db.Factory, new ArticleConstitution(), clock),
            new SqliteObservationService(db.Factory, new RecordingIncidentService(), clock),
            audit, needs, signals, scheduler, missions, curiosity, situation, resources,
            AttentionPolicy.Default, clock);

        return new World(review, cycles, audit, needs, scheduler, missions, planner, clock);
    }

    private static ReviewRequest Ask() => new(Caller, Lisbon);

    [Fact]
    public async Task AReviewGoesThroughTheCycleRatherThanBeingAQuery()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        ReviewOutcome outcome = await world.Review.ReviewAsync(Ask(), Ct);

        IReadOnlyList<CycleStageRecord> stages = await world.Cycles.StagesAsync(outcome.CycleId, Ct);

        // A briefing is a claim about what happened, and a claim is something Aurora decides to
        // make and is then accountable for.
        foreach (var stage in CycleStage.Order)
        {
            CycleStageRecord? record = stages.FirstOrDefault(s => s.Stage == stage);
            Assert.NotNull(record);
            Assert.True(record!.Status is StageStatus.Done or StageStatus.Omitted, $"{stage} was {record.Status}");
        }

        Assert.Contains(CycleStage.Decision, outcome.StagesRun);
        Assert.Contains(CycleStage.Policy, outcome.StagesRun);
        Assert.Contains(CycleStage.Observation, outcome.StagesRun);
        Assert.Contains(CycleStage.Reflection, outcome.StagesRun);

        CognitiveCycle? closed = await world.Cycles.GetAsync(outcome.CycleId, Ct);
        Assert.Equal(CycleStatus.Completed, closed!.Status);
    }

    [Fact]
    public async Task AReviewReadsRecordsAndNotRecollections()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        ReviewOutcome outcome = await world.Review.ReviewAsync(Ask(), Ct);

        // Recalling memories here would mix what Aurora believes into a report about what it did,
        // which is the confusion the review exists to prevent. Omitted with the reason, not skipped.
        Assert.Contains(CycleStage.Memory, outcome.StagesOmitted);
        Assert.Contains(CycleStage.Capabilities, outcome.StagesOmitted);
        Assert.Contains(CycleStage.Learning, outcome.StagesOmitted);
    }

    [Fact]
    public async Task TheBriefingCountsWhatTheStoresActuallyHold()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        await world.Audit.AppendAsync(
            new AuditEntry(
                Caller.ClientId, Caller.OsUser, "echo.say", "hash", "completed",
                Risk: "Low", Via: ResolutionVia.Explicit, Decision: "auto_low", PolicyIds: "p"),
            Ct);

        await world.Needs.DetectAsync(new NeedsSnapshot(DeadLetters: 2), [], Ct);

        ReviewOutcome outcome = await world.Review.ReviewAsync(Ask(), Ct);

        Assert.Equal(1, outcome.Findings.AuditEntries);
        Assert.Single(outcome.Findings.OpenNeeds);

        // Every line is checkable against the store it came from; nothing here interprets.
        Assert.Contains("1 audited action(s)", outcome.Summary);
        Assert.Contains("1 open need(s)", outcome.Summary);
    }

    [Fact]
    public async Task AReviewNamesGoalsNobodyHasLookedAtSinceTheyWerePromisedAReview()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        Mission mission = await world.Missions.CreateAsync(
            new MissionDefinition(
                "keep the owner organised", "less to hold in their head",
                "the owner stops being surprised", ["reads nothing on the owner's behalf"], "paulo"),
            "approval/1", Ct);

        Plan drifting = await world.Planner.CreateAsync(
            new GoalRequest("tidy the desk", "the desk is tidy", "paulo", ["nothing on it"], []),
            [new TaskRequest("tidy", "do it", TaskKind.Human, [], "LOW")], Ct);

        world.Clock.UtcNow = At("2026-03-01T14:00:00+00:00");

        ReviewOutcome outcome = await world.Review.ReviewAsync(Ask(), Ct);

        Assert.Contains(drifting.GoalId, outcome.Findings.GoalsPastReview);
        Assert.NotEmpty(mission.Id);
    }

    [Fact]
    public async Task AReviewSurvivesRestartAndCanBeReadBack()
    {
        using var db = new SqliteTestDb();

        ReviewOutcome outcome;
        {
            World first = Build(db);
            outcome = await first.Review.ReviewAsync(Ask(), Ct);
        }

        // A fresh set of services over the same database, as a process would after a restart.
        World second = Build(db);
        CognitiveCycle? cycle = await second.Cycles.GetAsync(outcome.CycleId, Ct);

        Assert.NotNull(cycle);
        Assert.Equal(CycleStatus.Completed, cycle!.Status);
        Assert.True(cycle.Executed);

        AuditVerification verified = await second.Audit.VerifyChainAsync(Ct);
        Assert.True(verified.Ok);
    }

    [Fact]
    public async Task AReviewResumesFromWhereTheLastOneStopped()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        ReviewOutcome first = await world.Review.ReviewAsync(Ask(), Ct);

        // The first review audits itself, so a second one from the same cursor would count it
        // again. Resuming from the cursor is what makes the briefing about what is new.
        ReviewOutcome second = await world.Review.ReviewAsync(
            Ask() with { AfterAuditSequence = first.Findings.HighestAuditSequence }, Ct);

        Assert.True(second.Findings.HighestAuditSequence >= first.Findings.HighestAuditSequence);
        Assert.True(second.Findings.AuditEntries < first.Findings.AuditEntries + 2);
    }
}
