using Aurora.Adapters.Planning;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>Conformance tests for RFC 05.</summary>
public sealed class PlannerTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");

    private static SqlitePlanner New(SqliteTestDb db, DateTimeOffset? now = null) =>
        new(db.Factory, new TestClock(now ?? Now));

    private static GoalRequest Request(
        string outcome = "The report is filed with the regulator",
        IReadOnlyList<string>? criteria = null,
        IReadOnlyList<string>? assumptions = null,
        string? deadline = null) =>
        new("File the quarterly report", outcome, "owner",
            criteria ?? ["regulator confirms receipt"],
            assumptions ?? ["the regulator's portal is available"],
            DeadlineAtUtc: deadline);

    private static TaskRequest Task(
        string title, string kind = TaskKind.Research, string? idempotencyKey = null,
        IReadOnlyList<string>? acceptance = null, params string[] dependsOn) =>
        new(title, $"do {title}", kind, dependsOn, "LOW",
            IdempotencyKey: idempotencyKey, AcceptanceTests: acceptance);

    private static TransitionEvidence Evidence(
        IReadOnlyList<AcceptanceResult>? acceptance = null, string? overrideRule = null) =>
        new(["evidence/1"], acceptance, "moved", overrideRule);

    // ---- rule 1: an objective states its outcome and how it is known to be met ----

    [Fact]
    public async Task AGoalWithoutSuccessCriteriaProducesADiscoveryPlan()
    {
        using var db = new SqliteTestDb();
        var planner = New(db);

        Plan plan = await planner.CreateAsync(
            Request(criteria: []), [Task("do the work")], Ct);

        Goal goal = (await planner.GetGoalAsync(plan.GoalId, Ct))!;
        IReadOnlyList<PlannedTask> tasks = await planner.ForGoalAsync(plan.GoalId, Ct);

        // "Deal with it" becomes a plan for finding out, not a decomposition built on a guess.
        Assert.Equal(GoalStatus.Draft, goal.Status);
        Assert.Single(tasks);
        Assert.Equal(Assignee.Human, tasks[0].AssignedTo);
        Assert.Contains("Clarify", tasks[0].Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADiscoveryPlanStatesWhyItIsOne()
    {
        using var db = new SqliteTestDb();

        Plan plan = await New(db).CreateAsync(Request(outcome: "  "), [Task("x")], Ct);

        Assert.Contains(plan.Assumptions, a => a.Contains("not yet defined", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACompleteGoalIsActiveAndDecomposed()
    {
        using var db = new SqliteTestDb();
        var planner = New(db);

        Plan plan = await planner.CreateAsync(
            Request(), [Task("gather figures"), Task("submit", dependsOn: "gather figures")], Ct);

        Assert.Equal(GoalStatus.Active, (await planner.GetGoalAsync(plan.GoalId, Ct))!.Status);
        Assert.Equal(2, (await planner.ForGoalAsync(plan.GoalId, Ct)).Count);
    }

    // ---- rule 5: assumptions are not hidden ----

    [Fact]
    public async Task AssumptionsSurviveAReplan()
    {
        using var db = new SqliteTestDb();
        var planner = New(db);
        Plan first = await planner.CreateAsync(Request(), [Task("a")], Ct);

        PlanRevision revision = await planner.ReplanAsync(
            first.GoalId, "the portal changed", [Task("b")], Ct);

        // Losing them at a revision is how a plan quietly stops explaining itself.
        Assert.Equal(first.Assumptions, revision.Current.Assumptions);
        Assert.Equal(2, revision.Current.Revision);
        Assert.Contains("the portal changed", revision.Current.Rationale, StringComparison.Ordinal);
    }

    // ---- rule 2: state machine plus evidence ----

    [Fact]
    public async Task AnIllegalTransitionIsRefused()
    {
        using var db = new SqliteTestDb();
        var planner = New(db);
        Plan plan = await planner.CreateAsync(Request(), [Task("a")], Ct);
        var task = (await planner.ForGoalAsync(plan.GoalId, Ct))[0];

        // READY does not jump straight to SUCCEEDED.
        await Assert.ThrowsAsync<PlanningException>(
            () => planner.TransitionAsync(task.Id, TaskState.Succeeded, Evidence(), Ct));
    }

    [Fact]
    public async Task ATransitionWithoutEvidenceIsRefused()
    {
        using var db = new SqliteTestDb();
        var planner = New(db);
        Plan plan = await planner.CreateAsync(Request(), [Task("a")], Ct);
        var task = (await planner.ForGoalAsync(plan.GoalId, Ct))[0];

        await Assert.ThrowsAsync<PlanningException>(() => planner.TransitionAsync(
            task.Id, TaskState.Running, new TransitionEvidence([]), Ct));
    }

    // ---- rule 3: RUNNING needs its dependencies ----

    [Fact]
    public async Task ATaskCannotRunWithAnUnmetDependency()
    {
        using var db = new SqliteTestDb();
        var planner = New(db);
        Plan plan = await planner.CreateAsync(
            Request(), [Task("first"), Task("second", dependsOn: "first")], Ct);
        var second = (await planner.ForGoalAsync(plan.GoalId, Ct)).First(t => t.Title == "second");

        await planner.TransitionAsync(second.Id, TaskState.Ready, Evidence(), Ct);

        await Assert.ThrowsAsync<PlanningException>(
            () => planner.TransitionAsync(second.Id, TaskState.Running, Evidence(), Ct));
    }

    [Fact]
    public async Task AnExplicitRuleMayPermitRunningWithUnmetDependencies()
    {
        using var db = new SqliteTestDb();
        var planner = New(db);
        Plan plan = await planner.CreateAsync(
            Request(), [Task("first"), Task("second", dependsOn: "first")], Ct);
        var second = (await planner.ForGoalAsync(plan.GoalId, Ct)).First(t => t.Title == "second");
        await planner.TransitionAsync(second.Id, TaskState.Ready, Evidence(), Ct);

        // The exception exists, and it is recorded rather than assumed.
        PlannedTask running = await planner.TransitionAsync(
            second.Id, TaskState.Running, Evidence(overrideRule: "rule/parallel-drafting"), Ct);

        Assert.Equal(TaskState.Running, running.Status);
    }

    // ---- invalid output does not become success ----

    [Fact]
    public async Task AFailedAcceptanceTestBecomesFailureWithADiagnosis()
    {
        using var db = new SqliteTestDb();
        var planner = New(db);
        Plan plan = await planner.CreateAsync(
            Request(), [Task("a", acceptance: ["schema valid"])], Ct);
        var task = (await planner.ForGoalAsync(plan.GoalId, Ct))[0];
        await planner.TransitionAsync(task.Id, TaskState.Running, Evidence(), Ct);

        PlannedTask outcome = await planner.TransitionAsync(
            task.Id, TaskState.Succeeded,
            Evidence(acceptance: [new AcceptanceResult("schema valid", Passed: false, "missing field")]), Ct);

        Assert.Equal(TaskState.Failed, outcome.Status);
        Assert.Contains("schema valid", outcome.Diagnosis!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnevaluatedAcceptanceTestIsNotTreatedAsPassing()
    {
        using var db = new SqliteTestDb();
        var planner = New(db);
        Plan plan = await planner.CreateAsync(
            Request(), [Task("a", acceptance: ["schema valid"])], Ct);
        var task = (await planner.ForGoalAsync(plan.GoalId, Ct))[0];
        await planner.TransitionAsync(task.Id, TaskState.Running, Evidence(), Ct);

        // Silence is not a pass.
        PlannedTask outcome = await planner.TransitionAsync(task.Id, TaskState.Succeeded, Evidence(), Ct);

        Assert.Equal(TaskState.Failed, outcome.Status);
        Assert.Contains("not evaluated", outcome.Diagnosis!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PassingAcceptanceSucceeds()
    {
        using var db = new SqliteTestDb();
        var planner = New(db);
        Plan plan = await planner.CreateAsync(
            Request(), [Task("a", acceptance: ["schema valid"])], Ct);
        var task = (await planner.ForGoalAsync(plan.GoalId, Ct))[0];
        await planner.TransitionAsync(task.Id, TaskState.Running, Evidence(), Ct);

        PlannedTask outcome = await planner.TransitionAsync(
            task.Id, TaskState.Succeeded,
            Evidence(acceptance: [new AcceptanceResult("schema valid", Passed: true)]), Ct);

        Assert.Equal(TaskState.Succeeded, outcome.Status);
        Assert.Null(outcome.Diagnosis);
    }

    // ---- failed dependency blocks descendants ----

    [Fact]
    public async Task AFailedTaskHoldsItsDependentsBack()
    {
        using var db = new SqliteTestDb();
        var planner = New(db);
        Plan plan = await planner.CreateAsync(
            Request(), [Task("first"), Task("second", dependsOn: "first")], Ct);
        IReadOnlyList<PlannedTask> tasks = await planner.ForGoalAsync(plan.GoalId, Ct);
        var first = tasks.First(t => t.Title == "first");
        var second = tasks.First(t => t.Title == "second");
        await planner.TransitionAsync(second.Id, TaskState.Ready, Evidence(), Ct);

        await planner.TransitionAsync(first.Id, TaskState.Running, Evidence(), Ct);
        await planner.TransitionAsync(first.Id, TaskState.Failed, Evidence(), Ct);

        PlannedTask held = (await planner.GetAsync(second.Id, Ct))!;

        // Nothing is marked complete by inference, and nothing proceeds by inference either.
        Assert.NotEqual(TaskState.Succeeded, held.Status);
        Assert.NotEqual(TaskState.Ready, held.Status);
        Assert.Contains("dependency", held.Diagnosis!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- rule 4: automatic repetition is narrow ----

    [Fact]
    public async Task ANonIdempotentTaskIsNotRetriedAutomatically()
    {
        using var db = new SqliteTestDb();
        var planner = New(db);
        Plan plan = await planner.CreateAsync(Request(), [Task("send the email", TaskKind.Tool)], Ct);
        var task = (await planner.ForGoalAsync(plan.GoalId, Ct))[0];
        await planner.TransitionAsync(task.Id, TaskState.Running, Evidence(), Ct);
        await planner.TransitionAsync(task.Id, TaskState.Failed, Evidence(), Ct);

        await Assert.ThrowsAsync<PlanningException>(() => planner.RetryAsync(task.Id, withinBudget: true, Ct));
    }

    [Fact]
    public async Task AnIdempotentTaskOutOfBudgetIsNotRetried()
    {
        using var db = new SqliteTestDb();
        var planner = New(db);
        Plan plan = await planner.CreateAsync(
            Request(), [Task("fetch", TaskKind.Tool, idempotencyKey: "k1")], Ct);
        var task = (await planner.ForGoalAsync(plan.GoalId, Ct))[0];
        await planner.TransitionAsync(task.Id, TaskState.Running, Evidence(), Ct);
        await planner.TransitionAsync(task.Id, TaskState.Failed, Evidence(), Ct);

        await Assert.ThrowsAsync<PlanningException>(() => planner.RetryAsync(task.Id, withinBudget: false, Ct));
    }

    [Fact]
    public async Task AnIdempotentTaskWithinBudgetIsRetried()
    {
        using var db = new SqliteTestDb();
        var planner = New(db);
        Plan plan = await planner.CreateAsync(
            Request(), [Task("fetch", TaskKind.Tool, idempotencyKey: "k1")], Ct);
        var task = (await planner.ForGoalAsync(plan.GoalId, Ct))[0];
        await planner.TransitionAsync(task.Id, TaskState.Running, Evidence(), Ct);
        await planner.TransitionAsync(task.Id, TaskState.Failed, Evidence(), Ct);

        Assert.Equal(TaskState.Ready, (await planner.RetryAsync(task.Id, withinBudget: true, Ct)).Status);
    }

    // ---- a blocked goal is not decomposed around ----

    [Fact]
    public async Task ABlockedGoalIsNotReplanned()
    {
        using var db = new SqliteTestDb();
        var planner = New(db);
        Plan plan = await planner.CreateAsync(Request(), [Task("a")], Ct);
        await planner.BlockAsync(plan.GoalId, "policy forbids sending to that recipient", Ct);

        PlanningException error = await Assert.ThrowsAsync<PlanningException>(
            () => planner.ReplanAsync(plan.GoalId, "try another way", [Task("b")], Ct));

        Assert.Contains("BLOCKED", error.Message, StringComparison.Ordinal);
    }

    // ---- deadlines are never quietly moved ----

    [Fact]
    public async Task AnOverdueGoalIsReportedAndItsDeadlineIsUntouched()
    {
        using var db = new SqliteTestDb();
        var planner = New(db);
        var deadline = Now.AddDays(1).ToString("O");
        Plan plan = await planner.CreateAsync(Request(deadline: deadline), [Task("a")], Ct);

        var later = New(db, Now.AddDays(5));
        IReadOnlyList<OverdueGoal> overdue = await later.HandleOverdueAsync(DeadlineAction.Pause, Ct);

        Assert.Single(overdue);
        Assert.Equal(GoalStatus.Paused, (await later.GetGoalAsync(plan.GoalId, Ct))!.Status);

        // Pausing is honest; moving the date so nothing looks late is not.
        Assert.Equal(deadline, (await later.GetGoalAsync(plan.GoalId, Ct))!.DeadlineAtUtc);
    }

    // ---- the scheduler ----

    [Fact]
    public async Task OnlyTasksWithSatisfiedDependenciesAreRunnable()
    {
        using var db = new SqliteTestDb();
        var planner = New(db);
        Plan plan = await planner.CreateAsync(
            Request(), [Task("first"), Task("second", dependsOn: "first")], Ct);
        IReadOnlyList<PlannedTask> tasks = await planner.ForGoalAsync(plan.GoalId, Ct);
        var first = tasks.First(t => t.Title == "first");

        Assert.Equal(["first"], (await planner.NextRunnableAsync(Ct)).Select(t => t.Title));

        await planner.TransitionAsync(first.Id, TaskState.Running, Evidence(), Ct);
        await planner.TransitionAsync(first.Id, TaskState.Succeeded, Evidence(), Ct);
        await planner.TransitionAsync(
            tasks.First(t => t.Title == "second").Id, TaskState.Ready, Evidence(), Ct);

        Assert.Equal(["second"], (await planner.NextRunnableAsync(Ct)).Select(t => t.Title));
    }

    [Fact]
    public async Task ADependencyOnAnUnknownTaskIsRefused()
    {
        using var db = new SqliteTestDb();

        await Assert.ThrowsAsync<PlanningException>(() => New(db).CreateAsync(
            Request(), [Task("second", dependsOn: "a task that was never described")], Ct));
    }
}
