using System.Globalization;
using Aurora.Adapters.Cognition;
using Aurora.Adapters.Curiosity;
using Aurora.Adapters.Needs;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Planning;
using Aurora.Adapters.Resources;
using Aurora.Adapters.Signals;
using Aurora.Adapters.Situation;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Governed curiosity (RFC 032): autonomy limited by rule.
/// </summary>
/// <remarks>
/// This is where "limited autonomy" is either real or decorative, so most of these tests are about
/// what curiosity cannot reach rather than what it can.
/// </remarks>
public sealed class CuriosityTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Lisbon = "Europe/Lisbon";

    private static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record World(
        SqliteCuriosityEngine Curiosity, SqlitePlanner Planner, SqliteNeedsService Needs,
        SituationService Situation, FakeResourceProbe Probe, TestClock Clock);

    private static World Build(SqliteTestDb db, string now = "2026-01-15T14:00:00+00:00")
    {
        var clock = new TestClock(At(now));
        var cycles = new SqliteCognitiveCycle(db.Factory, clock);
        var planner = new SqlitePlanner(db.Factory, clock, TestBus.Over(db.Factory, clock));
        var needs = new SqliteNeedsService(db.Factory, planner, clock);
        var signals = new SqliteSignalService(db.Factory, cycles, clock);
        var probe = new FakeResourceProbe();
        var resources = new SystemResourceModel(probe, clock);
        var situation = new SituationService(signals, needs, resources, QuietHours.Default, clock);

        return new World(
            new SqliteCuriosityEngine(db.Factory, planner, resources, situation, needs, clock),
            planner, needs, situation, probe, clock);
    }

    private static KnowledgeGap Gap(
        string source = "aurora/memory", string sensitivity = Sensitivity.Public,
        int seen = 5, double confidence = 0.2, string subject = "topic/rust") =>
        new(subject, "what does the owner actually use Rust for?", seen, confidence,
            ["conversation/3", "conversation/7"], source, sensitivity);

    private static Task<SituationAssessment> CalmAsync(World world) =>
        world.Situation.AssessAsync(
            new SituationContext(Lisbon, UserAvailability: UserAvailability.Online), Ct);

    // ---- rule 1: an allowlist, and only an allowlist ----

    [Fact]
    public async Task ASourceNobodyPermittedIsRefusedAndSaysSo()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        CuriosityProposal proposal = Assert.Single(await world.Curiosity.DetectAsync(
            new CuriositySnapshot([Gap(source: "the whole internet")]), CuriosityPolicy.Default, Ct));

        Assert.Equal(CuriosityStatus.Rejected, proposal.Status);
        Assert.Contains(CuriosityRefusal.SourceNotPermitted, proposal.RefusalReasons);

        // Refused, and recorded. A question that vanished without a reason is one nobody can argue
        // about later.
        Assert.NotNull(await world.Curiosity.GetAsync(proposal.Id, Ct));
    }

    [Fact]
    public void TheDefaultPolicyReachesAuroraSOwnRecordsAndNothingFurther()
    {
        // Shipping an open default would make every later restriction something somebody has to
        // remember to add.
        Assert.Equal(["aurora/memory", "aurora/world"], CuriosityPolicy.Default.AllowedSources);
        Assert.Equal(Sensitivity.Public, CuriosityPolicy.Default.SensitivityCeiling);
    }

    [Fact]
    public async Task AQuestionAboutSomethingClassifiedIsRefused()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        CuriosityProposal proposal = Assert.Single(await world.Curiosity.DetectAsync(
            new CuriositySnapshot([Gap(sensitivity: Sensitivity.Confidential)]),
            CuriosityPolicy.Default, Ct));

        Assert.Equal(CuriosityStatus.Rejected, proposal.Status);
        Assert.Contains(CuriosityRefusal.AboveSensitivityCeiling, proposal.RefusalReasons);
    }

    // ---- what is worth asking at all ----

    [Fact]
    public async Task SomethingSeenOnceIsNotAPatternAndSomethingAlreadyKnownIsNotAGap()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        Assert.Empty(await world.Curiosity.DetectAsync(
            new CuriositySnapshot([Gap(seen: 1)]), CuriosityPolicy.Default, Ct));

        Assert.Empty(await world.Curiosity.DetectAsync(
            new CuriositySnapshot([Gap(confidence: 0.9)]), CuriosityPolicy.Default, Ct));
    }

    [Fact]
    public async Task TheSameGapSeenAgainIsOneQuestionRatherThanTwo()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        CuriosityProposal first = Assert.Single(await world.Curiosity.DetectAsync(
            new CuriositySnapshot([Gap()]), CuriosityPolicy.Default, Ct));
        CuriosityProposal again = Assert.Single(await world.Curiosity.DetectAsync(
            new CuriositySnapshot([Gap(seen: 9)]), CuriosityPolicy.Default, Ct));

        Assert.Equal(first.Id, again.Id);
    }

    // ---- rule 2: curiosity can only ever build one thing ----

    [Fact]
    public async Task AnApprovedQuestionBecomesADraftGoalOfResearchAndNothingElse()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        CuriosityProposal proposal = Assert.Single(await world.Curiosity.DetectAsync(
            new CuriositySnapshot([Gap()]), CuriosityPolicy.Default, Ct));

        Assert.Equal(CuriosityStatus.Candidate, proposal.Status);

        CuriosityProposal scheduled = await world.Curiosity.ScheduleAsync(proposal.Id, "approval/1", Ct);
        Goal goal = (await world.Planner.GetGoalAsync(scheduled.GoalRef!, Ct))!;

        // DRAFT, with no plan and no tasks. There is no path from curiosity to a tool call, an
        // account, a message or a purchase — not because those are checked for, but because a
        // drafted research goal is the only thing it can produce.
        Assert.Equal(GoalStatus.Draft, goal.Status);
        Assert.Null(await world.Planner.GetActivePlanAsync(goal.Id, Ct));
        Assert.Empty(await world.Planner.ForGoalAsync(goal.Id, Ct));
    }

    [Fact]
    public async Task EvenAPermittedPublicCheapQuestionNeedsTheOwnerToAgree()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        CuriosityProposal proposal = Assert.Single(await world.Curiosity.DetectAsync(
            new CuriositySnapshot([Gap()]), CuriosityPolicy.Default, Ct));

        // It is still Aurora spending the owner's resources on something the owner did not ask for.
        Assert.True(proposal.ApprovalRequired);
        await Assert.ThrowsAsync<CuriosityException>(() =>
            world.Curiosity.ScheduleAsync(proposal.Id, "", Ct));
    }

    [Fact]
    public async Task ARefusedQuestionCannotBeScheduledByAskingAgain()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        CuriosityProposal refused = Assert.Single(await world.Curiosity.DetectAsync(
            new CuriositySnapshot([Gap(source: "the whole internet")]), CuriosityPolicy.Default, Ct));

        await Assert.ThrowsAsync<CuriosityException>(() =>
            world.Curiosity.ScheduleAsync(refused.Id, "approval/1", Ct));
    }

    // ---- rule 3: curiosity is the first thing to give way ----

    [Fact]
    public async Task AQuestionIsBlockedWhenThereIsNoCapacityToSpare()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        CuriosityProposal proposal = Assert.Single(await world.Curiosity.DetectAsync(
            new CuriositySnapshot([Gap()]), CuriosityPolicy.Default, Ct));

        SituationAssessment calm = await CalmAsync(world);
        DecisionOption available = await world.Curiosity.EvaluateAsync(
            proposal.Id, calm, ResourceBudget.Default, Ct);

        Assert.Empty(available.BlockingReasons);

        // Under pressure, discretionary work is what goes first.
        world.Probe.Cpu = 0.9;

        DecisionOption blocked = await world.Curiosity.EvaluateAsync(
            proposal.Id, calm, ResourceBudget.Default, Ct);

        Assert.Contains(CuriosityRefusal.NoResources, blocked.BlockingReasons);
    }

    [Fact]
    public async Task AQuestionWaitsWhileSomethingIsActuallyWrong()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        CuriosityProposal proposal = Assert.Single(await world.Curiosity.DetectAsync(
            new CuriositySnapshot([Gap()]), CuriosityPolicy.Default, Ct));

        SituationAssessment calm = await CalmAsync(world);
        await world.Needs.DetectAsync(new NeedsSnapshot(UnreconciledReservations: 2), [], Ct);

        DecisionOption option = await world.Curiosity.EvaluateAsync(
            proposal.Id, calm, ResourceBudget.Default, Ct);

        // An open incident means the system is failing a promise it already made. A question can
        // wait for that.
        Assert.Contains(CuriosityRefusal.OutrankedByNeeds, option.BlockingReasons);
    }

    [Fact]
    public async Task WeighingAQuestionDoesNotHoldOnToCapacityWhileItIsBeingWeighed()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        CuriosityProposal proposal = Assert.Single(await world.Curiosity.DetectAsync(
            new CuriositySnapshot([Gap()]), CuriosityPolicy.Default, Ct));

        await world.Curiosity.EvaluateAsync(proposal.Id, await CalmAsync(world), ResourceBudget.Default, Ct);
        await world.Curiosity.EvaluateAsync(proposal.Id, await CalmAsync(world), ResourceBudget.Default, Ct);

        // Holding a slot while a decision is still being made would be curiosity taking capacity
        // for a question nobody has agreed to ask.
        DecisionOption third = await world.Curiosity.EvaluateAsync(
            proposal.Id, await CalmAsync(world), ResourceBudget.Default, Ct);

        Assert.DoesNotContain(CuriosityRefusal.NoResources, third.BlockingReasons);
    }

    // ---- rule 4: researching does not create knowledge ----

    [Fact]
    public async Task AResultIsRecordedByReferenceAndIsNotThereforeBelieved()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        CuriosityProposal proposal = Assert.Single(await world.Curiosity.DetectAsync(
            new CuriositySnapshot([Gap()]), CuriosityPolicy.Default, Ct));

        await world.Curiosity.ScheduleAsync(proposal.Id, "approval/1", Ct);
        CuriosityProposal learned = await world.Curiosity.RecordResultAsync(
            proposal.Id, "observation/42", Ct);

        // LEARNED means the question was investigated and the answer is on file — not that Aurora
        // now believes it. Turning an observation into a memory is a separate act with its own
        // provenance and its own anchor.
        Assert.Equal(CuriosityStatus.Learned, learned.Status);
        Assert.Contains("observation/42", learned.ResultRefs);
    }

    [Fact]
    public void TheCuriosityEngineHasNoWayToWriteAMemory()
    {
        // Rule 4, enforced by construction rather than by discipline: nothing on the interface or
        // in the implementation's dependencies can reach memory, so research cannot quietly become
        // belief no matter what a later change does inside it.
        var dependencies = typeof(SqliteCuriosityEngine)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToList();

        Assert.DoesNotContain(typeof(IMemoryService), dependencies);
        Assert.DoesNotContain(typeof(IKnowledgeGraph), dependencies);
    }

    // ---- a queue of questions, not a wishlist ----

    [Fact]
    public async Task AQuestionNobodyActsOnExpires()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        CuriosityProposal proposal = Assert.Single(await world.Curiosity.DetectAsync(
            new CuriositySnapshot([Gap()]), CuriosityPolicy.Default, Ct));

        world.Clock.UtcNow = At("2026-02-15T14:00:00+00:00");

        Assert.Equal(1, await world.Curiosity.ExpireDueAsync(Ct));
        Assert.Equal(CuriosityStatus.Expired, (await world.Curiosity.GetAsync(proposal.Id, Ct))!.Status);
    }
}
