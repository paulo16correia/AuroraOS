using System.Globalization;
using Aurora.Adapters.Missions;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Planning;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Missions (RFC 052): purpose above goals, decided by the person.
/// </summary>
public sealed class MissionTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static (SqliteMissionService Missions, SqlitePlanner Planner, TestClock Clock) Build(
        SqliteTestDb db, string now = "2026-01-15T09:00:00+00:00")
    {
        var clock = new TestClock(At(now));
        var planner = new SqlitePlanner(db.Factory, clock);

        return (new SqliteMissionService(db.Factory, planner, clock), planner, clock);
    }

    private static MissionDefinition Organised() =>
        new("keep the owner organised",
            "reduce the number of things the owner has to hold in their head",
            "the owner stops being surprised by things they had already decided",
            ["never reads or sends anything on the owner's behalf without a separate approval"],
            Owner: "paulo");

    private static GoalRequest WellSpecified(string? mission = null) =>
        new("organise this week's email", "the inbox is triaged", "paulo",
            ["nothing older than a week is untriaged"], [], MissionRef: mission);

    // ---- rule 4: Aurora does not decide what it is for ----

    [Fact]
    public async Task AMissionCannotBeCreatedOrChangedBySystem()
    {
        using var db = new SqliteTestDb();
        var (missions, _, _) = Build(db);

        await Assert.ThrowsAsync<MissionException>(() =>
            missions.CreateAsync(Organised() with { Owner = NeedOwner.System }, "approval/1", Ct));

        Mission mission = await missions.CreateAsync(Organised(), "approval/1", Ct);

        // A system that could revise its own purpose would have, in the only sense that matters,
        // no purpose at all.
        await Assert.ThrowsAsync<MissionException>(() =>
            missions.PauseAsync(mission.Id, NeedOwner.System, Ct));
    }

    [Fact]
    public async Task AMissionNeedsTheOwnerSApprovalToExist()
    {
        using var db = new SqliteTestDb();
        var (missions, _, _) = Build(db);

        await Assert.ThrowsAsync<MissionException>(() =>
            missions.CreateAsync(Organised(), "", Ct));
    }

    [Fact]
    public async Task AMissionWithNoStatedEdgeIsRefused()
    {
        using var db = new SqliteTestDb();
        var (missions, _, _) = Build(db);

        // A purpose with no boundary quietly grows one. They are written down when the mission is
        // defined, rather than argued about after something has already happened.
        await Assert.ThrowsAsync<MissionException>(() =>
            missions.CreateAsync(Organised() with { Boundaries = [] }, "approval/1", Ct));
    }

    // ---- rule 2: no persistent goal belongs to nobody ----

    [Fact]
    public async Task AGoalThatServesNoMissionGetsADateSomebodyHasToLookAtItAgain()
    {
        using var db = new SqliteTestDb();
        var (_, planner, _) = Build(db);

        Plan plan = await planner.CreateAsync(
            WellSpecified(),
            [new TaskRequest("triage", "sort the inbox", TaskKind.Human, [], "LOW")], Ct);

        Goal goal = (await planner.GetGoalAsync(plan.GoalId, Ct))!;

        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.Null(goal.MissionRef);

        // What rule 2 actually prevents: a standing commitment nobody owns and nobody ever looks
        // at again.
        Assert.NotNull(goal.AdHocReviewAtUtc);
        Assert.Equal(At("2026-02-14T09:00:00+00:00"), DateTimeOffset.Parse(goal.AdHocReviewAtUtc!));
    }

    [Fact]
    public async Task ADraftGoalIsNotYetACommitmentAndNeedsNoReviewDate()
    {
        using var db = new SqliteTestDb();
        var (_, planner, _) = Build(db);

        Goal draft = await planner.DraftAsync(WellSpecified(), Ct);

        Assert.Equal(GoalStatus.Draft, draft.Status);
        Assert.Null(draft.AdHocReviewAtUtc);
    }

    [Fact]
    public async Task AligningAGoalStopsItDrifting()
    {
        using var db = new SqliteTestDb();
        var (missions, planner, _) = Build(db);

        Mission mission = await missions.CreateAsync(Organised(), "approval/1", Ct);
        Plan plan = await planner.CreateAsync(
            WellSpecified(), [new TaskRequest("triage", "sort", TaskKind.Human, [], "LOW")], Ct);

        Goal aligned = await missions.AlignAsync(plan.GoalId, mission.Id, "paulo", Ct);

        Assert.Equal(mission.Id, aligned.MissionRef);
        Assert.Null(aligned.AdHocReviewAtUtc);

        Goal stored = (await planner.GetGoalAsync(plan.GoalId, Ct))!;
        Assert.Equal(mission.Id, stored.MissionRef);
    }

    // ---- rule 1: a mission is not an execution order ----

    [Fact]
    public async Task AligningAGoalChangesWhatItIsForAndNotWhatItMayDo()
    {
        using var db = new SqliteTestDb();
        var (missions, planner, _) = Build(db);

        Mission mission = await missions.CreateAsync(Organised(), "approval/1", Ct);
        Plan plan = await planner.CreateAsync(
            WellSpecified(),
            [new TaskRequest("send the summary", "email it", TaskKind.Tool, [], "HIGH")], Ct);

        await missions.AlignAsync(plan.GoalId, mission.Id, "paulo", Ct);

        IReadOnlyList<PlannedTask> tasks = await planner.ForGoalAsync(plan.GoalId, Ct);
        PlannedTask risky = Assert.Single(tasks);

        // Nothing about the task's risk, approval or state moved because it now serves a purpose.
        // A mission does not stand in for the approval a risky task needs.
        Assert.Equal("HIGH", risky.Risk);
        Assert.NotEqual(TaskState.Running, risky.Status);
    }

    // ---- review reports, and changes nothing ----

    [Fact]
    public async Task AReviewNamesWhatIsDriftingWithoutDoingAnythingAboutIt()
    {
        using var db = new SqliteTestDb();
        var (missions, planner, clock) = Build(db);

        Mission mission = await missions.CreateAsync(
            Organised() with { ReviewAtUtc = "2026-02-01T00:00:00+00:00" }, "approval/1", Ct);

        Plan aligned = await planner.CreateAsync(
            WellSpecified(), [new TaskRequest("t", "d", TaskKind.Human, [], "LOW")], Ct);
        await missions.AlignAsync(aligned.GoalId, mission.Id, "paulo", Ct);

        Plan drifting = await planner.CreateAsync(
            WellSpecified() with { Title = "something else" },
            [new TaskRequest("t", "d", TaskKind.Human, [], "LOW")], Ct);

        clock.UtcNow = At("2026-03-01T09:00:00+00:00");

        MissionReview review = await missions.ReviewAsync(mission.Id, Ct);

        Assert.Contains(aligned.GoalId, review.AlignedGoalRefs);
        Assert.Contains(drifting.GoalId, review.AdHocGoalsPastReview);
        Assert.True(review.ReviewOverdue);

        // Named, and left alone. What to do about a drifting goal is the owner's call, and a review
        // that quietly retired things would be making it for them.
        Goal untouched = (await planner.GetGoalAsync(drifting.GoalId, Ct))!;
        Assert.Equal(GoalStatus.Active, untouched.Status);
    }

    [Fact]
    public async Task APausedMissionTakesOnNoNewGoals()
    {
        using var db = new SqliteTestDb();
        var (missions, planner, _) = Build(db);

        Mission mission = await missions.CreateAsync(Organised(), "approval/1", Ct);
        await missions.PauseAsync(mission.Id, "paulo", Ct);

        Plan plan = await planner.CreateAsync(
            WellSpecified(), [new TaskRequest("t", "d", TaskKind.Human, [], "LOW")], Ct);

        await Assert.ThrowsAsync<MissionException>(() =>
            missions.AlignAsync(plan.GoalId, mission.Id, "paulo", Ct));
    }

    [Fact]
    public async Task ARetiredMissionIsNotRevived()
    {
        using var db = new SqliteTestDb();
        var (missions, _, _) = Build(db);

        Mission mission = await missions.CreateAsync(Organised(), "approval/1", Ct);
        await missions.RetireAsync(mission.Id, "paulo", Ct);

        await Assert.ThrowsAsync<MissionException>(() =>
            missions.ActivateAsync(mission.Id, "paulo", Ct));
    }
}
