using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>Turns desired results into observable work (RFC 05).</summary>
public interface IPlanner
{
    /// <summary>
    /// Creates a goal and its first plan. A request without an outcome or success criteria produces
    /// a discovery plan asking for them, never a guess at what was meant.
    /// </summary>
    Task<Plan> CreateAsync(
        GoalRequest request, IReadOnlyList<TaskRequest> tasks, CancellationToken ct);

    /// <summary>
    /// Records a stated intention as a DRAFT goal, with no plan (RFC 10).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="CreateAsync"/> because arriving at a goal and deciding how to
    /// pursue it are two acts. A goal posted from outside is a request, not an instruction to
    /// start work; it waits in DRAFT until planning is a decision Aurora has actually made.
    /// </remarks>
    Task<Goal> DraftAsync(GoalRequest request, CancellationToken ct);

    /// <summary>Supersedes the active plan with a new revision, carrying assumptions forward.</summary>
    Task<PlanRevision> ReplanAsync(
        string goalId, string trigger, IReadOnlyList<TaskRequest> tasks, CancellationToken ct);

    /// <summary>Blocks a goal that policy will not permit, with the reason and no decomposition.</summary>
    Task<Goal> BlockAsync(string goalId, string reason, CancellationToken ct);

    /// <summary>Reports goals past their deadline and applies the configured action to each.</summary>
    Task<IReadOnlyList<OverdueGoal>> HandleOverdueAsync(string action, CancellationToken ct);

    Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct);

    Task<Plan?> GetActivePlanAsync(string goalId, CancellationToken ct);
}

/// <summary>Moves tasks through their state machine, with evidence (RFC 05).</summary>
public interface ITaskService
{
    Task<PlannedTask> TransitionAsync(
        string taskId, string targetState, TransitionEvidence evidence, CancellationToken ct);

    /// <summary>
    /// Repeats a task automatically. Allowed only for an idempotent task within budget (rule 4).
    /// </summary>
    Task<PlannedTask> RetryAsync(string taskId, bool withinBudget, CancellationToken ct);

    Task<PlannedTask?> GetAsync(string taskId, CancellationToken ct);

    Task<IReadOnlyList<PlannedTask>> ForGoalAsync(string goalId, CancellationToken ct);
}

/// <summary>Selects the tasks whose dependencies are satisfied (RFC 05).</summary>
public interface ITaskScheduler
{
    Task<IReadOnlyList<PlannedTask>> NextRunnableAsync(CancellationToken ct);
}
