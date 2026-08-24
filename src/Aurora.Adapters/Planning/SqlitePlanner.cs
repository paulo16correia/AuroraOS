using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Planning;

/// <summary>Objectives, plans and tasks (RFC 05).</summary>
public sealed class SqlitePlanner : IPlanner, ITaskService, ITaskScheduler
{
    private readonly SqliteConnectionFactory _factory;
    private readonly IClock _clock;

    public SqlitePlanner(SqliteConnectionFactory factory, IClock clock)
    {
        _factory = factory;
        _clock = clock;
    }

    // ---- IPlanner ----

    public async Task<Plan> CreateAsync(
        GoalRequest request, IReadOnlyList<TaskRequest> tasks, CancellationToken ct)
    {
        // Rule 1: an objective states an outcome and how it will be known to be met. "Deal with it"
        // becomes a plan for finding out, not a plan built on an assumed meaning.
        var needsDiscovery = string.IsNullOrWhiteSpace(request.Outcome)
                          || request.SuccessCriteria.Count == 0;

        var goalId = Guid.NewGuid().ToString("N");
        var goal = new Goal(
            goalId, request.Title, request.Outcome, request.OwnerId,
            Math.Clamp(request.Priority, 1, 5),
            needsDiscovery ? GoalStatus.Draft : GoalStatus.Active,
            request.ConstraintsJson, request.SuccessCriteria, request.DeadlineAtUtc,
            request.BudgetJson, request.CreatedFromRef, request.ApprovalPolicyId);

        await SaveGoalAsync(goal, ct).ConfigureAwait(false);

        IReadOnlyList<TaskRequest> planned = needsDiscovery
            ? [DiscoveryTask(request)]
            : tasks;

        if (planned.Count == 0)
        {
            throw new PlanningException("A plan needs at least one task.");
        }

        var rationale = needsDiscovery
            ? "The outcome or its success criteria were not stated; this plan asks for them."
            : "Decomposed from the stated outcome and success criteria.";

        // Rule 5: assumptions travel with the plan. A discovery plan states its own, since
        // "we do not yet know what done looks like" is exactly the kind of assumption that
        // changes cost, risk and direction.
        var assumptions = needsDiscovery
            ? request.Assumptions.Append("The outcome is not yet defined; nothing is decomposed on a guess.").ToList()
            : request.Assumptions.ToList();

        return await WritePlanAsync(goalId, revision: 1, rationale, assumptions, planned, ct)
            .ConfigureAwait(false);
    }

    public async Task<PlanRevision> ReplanAsync(
        string goalId, string trigger, IReadOnlyList<TaskRequest> tasks, CancellationToken ct)
    {
        Goal goal = await GetGoalAsync(goalId, ct).ConfigureAwait(false)
            ?? throw new PlanningException("Unknown goal.");

        if (goal.Status == GoalStatus.Blocked)
        {
            // Limit case: a goal policy will not permit stays blocked. Replanning it would be
            // decomposing around the rule rather than respecting it.
            throw new PlanningException(
                $"The goal is BLOCKED ({goal.BlockedReason}); it is not replanned around.");
        }

        Plan previous = await GetActivePlanAsync(goalId, ct).ConfigureAwait(false)
            ?? throw new PlanningException("There is no active plan to revise.");

        await ExecuteAsync("UPDATE plan SET status = @s WHERE id = @id;", ct,
            ("@s", PlanStatus.Superseded), ("@id", previous.Id)).ConfigureAwait(false);

        // Assumptions carry forward. Losing them at a revision is how a plan quietly stops
        // explaining itself.
        Plan current = await WritePlanAsync(
            goalId, previous.Revision + 1, $"Replanned: {trigger}", previous.Assumptions, tasks, ct)
            .ConfigureAwait(false);

        return new PlanRevision(previous, current, trigger);
    }

    public async Task<Goal> BlockAsync(string goalId, string reason, CancellationToken ct)
    {
        Goal goal = await GetGoalAsync(goalId, ct).ConfigureAwait(false)
            ?? throw new PlanningException("Unknown goal.");

        await ExecuteAsync(
            "UPDATE goal SET status = @s, blocked_reason = @r WHERE id = @id;", ct,
            ("@s", GoalStatus.Blocked), ("@r", reason), ("@id", goalId)).ConfigureAwait(false);

        return goal with { Status = GoalStatus.Blocked, BlockedReason = reason };
    }

    public async Task<IReadOnlyList<OverdueGoal>> HandleOverdueAsync(string action, CancellationToken ct)
    {
        var now = Iso(_clock.UtcNow);
        var overdue = new List<OverdueGoal>();

        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, deadline_at_utc FROM goal
                 WHERE deadline_at_utc IS NOT NULL AND deadline_at_utc < @now
                   AND status IN (@active, @draft);
                """;
            command.Parameters.AddWithValue("@now", now);
            command.Parameters.AddWithValue("@active", GoalStatus.Active);
            command.Parameters.AddWithValue("@draft", GoalStatus.Draft);

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                overdue.Add(new OverdueGoal(reader.GetString(0), reader.GetString(1), action));
            }
        }

        // The deadline itself is never touched. Notifying, pausing or continuing are all honest;
        // moving the date so nothing looks late is not.
        if (action == DeadlineAction.Pause)
        {
            foreach (OverdueGoal goal in overdue)
            {
                await ExecuteAsync("UPDATE goal SET status = @s WHERE id = @id;", ct,
                    ("@s", GoalStatus.Paused), ("@id", goal.GoalId)).ConfigureAwait(false);
            }
        }

        return overdue;
    }

    public async Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = GoalSelect + " WHERE id = @id;";
        command.Parameters.AddWithValue("@id", goalId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadGoal(reader) : null;
    }

    public async Task<Plan?> GetActivePlanAsync(string goalId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, goal_id, revision, rationale, assumptions, task_ids, status
              FROM plan WHERE goal_id = @g AND status IN (@proposed, @approved, @active)
             ORDER BY revision DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("@g", goalId);
        command.Parameters.AddWithValue("@proposed", PlanStatus.Proposed);
        command.Parameters.AddWithValue("@approved", PlanStatus.Approved);
        command.Parameters.AddWithValue("@active", PlanStatus.Active);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new Plan(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
                reader.GetString(4).Split('\n', StringSplitOptions.RemoveEmptyEntries),
                Split(reader.GetString(5)), reader.GetString(6))
            : null;
    }

    // ---- ITaskService ----

    public async Task<PlannedTask> TransitionAsync(
        string taskId, string targetState, TransitionEvidence evidence, CancellationToken ct)
    {
        PlannedTask task = await GetAsync(taskId, ct).ConfigureAwait(false)
            ?? throw new PlanningException("Unknown task.");

        // Rule 2: every transition respects the machine and attaches evidence. A move with nothing
        // behind it is an assertion, not a record.
        if (!TaskState.Allowed.TryGetValue(task.Status, out var allowed)
            || !allowed.Contains(targetState, StringComparer.Ordinal))
        {
            throw new PlanningException($"{task.Status} does not transition to {targetState}.");
        }

        if (evidence.Refs.Count == 0)
        {
            throw new PlanningException("A task transition must attach evidence.");
        }

        if (targetState == TaskState.Running)
        {
            IReadOnlyList<PlannedTask> siblings = await ForGoalAsync(task.GoalId, ct).ConfigureAwait(false);
            var unmet = task.Dependencies
                .Where(d => siblings.FirstOrDefault(s => s.Id == d)?.Status != TaskState.Succeeded)
                .ToList();

            // Rule 3: RUNNING never coexists with a dependency that has not succeeded, unless an
            // explicit rule says so — and that rule is recorded, not assumed.
            if (unmet.Count > 0 && string.IsNullOrWhiteSpace(evidence.DependencyOverrideRule))
            {
                throw new PlanningException(
                    $"Dependencies have not succeeded: {string.Join(", ", unmet)}.");
            }
        }

        var diagnosis = task.Diagnosis;

        if (targetState == TaskState.Succeeded)
        {
            var failed = (evidence.AcceptanceResults ?? [])
                .Where(r => !r.Passed).Select(r => r.Test).ToList();

            var missing = task.AcceptanceTests
                .Except((evidence.AcceptanceResults ?? []).Select(r => r.Test), StringComparer.Ordinal)
                .ToList();

            // Limit case: invalid output fails validation, records a diagnosis, and does not
            // unlock dependents. Marking it succeeded by inference is the failure being prevented.
            if (failed.Count > 0 || missing.Count > 0)
            {
                diagnosis = failed.Count > 0
                    ? $"acceptance failed: {string.Join(", ", failed)}"
                    : $"acceptance not evaluated: {string.Join(", ", missing)}";

                await ApplyAsync(task, TaskState.Failed, evidence, diagnosis, ct).ConfigureAwait(false);
                return (await GetAsync(taskId, ct).ConfigureAwait(false))!;
            }

            diagnosis = null;
        }

        await ApplyAsync(task, targetState, evidence, diagnosis, ct).ConfigureAwait(false);

        if (targetState == TaskState.Failed)
        {
            await HoldDependentsAsync(task, ct).ConfigureAwait(false);
        }

        return (await GetAsync(taskId, ct).ConfigureAwait(false))!;
    }

    public async Task<PlannedTask> RetryAsync(string taskId, bool withinBudget, CancellationToken ct)
    {
        PlannedTask task = await GetAsync(taskId, ct).ConfigureAwait(false)
            ?? throw new PlanningException("Unknown task.");

        // Rule 4: an automatic repetition is only safe when repeating is safe. Without an
        // idempotency key a retry might do the thing twice, and budget is the other half.
        if (string.IsNullOrWhiteSpace(task.IdempotencyKey))
        {
            throw new PlanningException("A task without an idempotency key is not repeated automatically.");
        }

        if (!withinBudget)
        {
            throw new PlanningException("The goal's budget does not allow another attempt.");
        }

        return await TransitionAsync(
            taskId, TaskState.Ready,
            new TransitionEvidence(["retry/automatic"], Note: "idempotent retry within budget"), ct)
            .ConfigureAwait(false);
    }

    public async Task<PlannedTask?> GetAsync(string taskId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = TaskSelect + " WHERE id = @id;";
        command.Parameters.AddWithValue("@id", taskId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadTask(reader) : null;
    }

    public async Task<IReadOnlyList<PlannedTask>> ForGoalAsync(string goalId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = TaskSelect + " WHERE goal_id = @g ORDER BY rowid ASC;";
        command.Parameters.AddWithValue("@g", goalId);

        var rows = new List<PlannedTask>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(ReadTask(reader));
        }

        return rows;
    }

    // ---- ITaskScheduler ----

    public async Task<IReadOnlyList<PlannedTask>> NextRunnableAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // Only tasks of goals that are actually running, and only those already READY.
        command.CommandText = TaskSelect
            + " WHERE status = @ready AND goal_id IN (SELECT id FROM goal WHERE status = @active);";
        command.Parameters.AddWithValue("@ready", TaskState.Ready);
        command.Parameters.AddWithValue("@active", GoalStatus.Active);

        var candidates = new List<PlannedTask>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                candidates.Add(ReadTask(reader));
            }
        }

        var runnable = new List<PlannedTask>();
        foreach (PlannedTask task in candidates)
        {
            IReadOnlyList<PlannedTask> siblings = await ForGoalAsync(task.GoalId, ct).ConfigureAwait(false);
            if (task.Dependencies.All(d =>
                    siblings.FirstOrDefault(s => s.Id == d)?.Status == TaskState.Succeeded))
            {
                runnable.Add(task);
            }
        }

        return runnable;
    }

    // ---- internals ----

    private static TaskRequest DiscoveryTask(GoalRequest request) => new(
        $"Clarify the outcome of '{request.Title}'",
        "The outcome or its success criteria were not stated. Ask for them before decomposing.",
        TaskKind.Human, Dependencies: [], Risk: "LOW", AssignedTo: Assignee.Human,
        AcceptanceTests: ["outcome stated", "success criteria stated"]);

    /// <summary>
    /// A failed task holds its dependents back rather than letting them look ready. RFC 05 forbids
    /// marking anything complete by inference, and the mirror of that is not letting anything
    /// proceed by inference either.
    /// </summary>
    private async Task HoldDependentsAsync(PlannedTask failed, CancellationToken ct)
    {
        IReadOnlyList<PlannedTask> siblings = await ForGoalAsync(failed.GoalId, ct).ConfigureAwait(false);

        foreach (PlannedTask dependent in siblings.Where(s =>
                     s.Dependencies.Contains(failed.Id, StringComparer.Ordinal)
                     && s.Status == TaskState.Ready))
        {
            await ExecuteAsync(
                "UPDATE planned_task SET status = @s, diagnosis = @d WHERE id = @id;", ct,
                ("@s", TaskState.Draft),
                ("@d", $"held: dependency {failed.Id} failed; replan or fix it"),
                ("@id", dependent.Id)).ConfigureAwait(false);
        }
    }

    private async Task ApplyAsync(
        PlannedTask task, string targetState, TransitionEvidence evidence, string? diagnosis, CancellationToken ct)
    {
        await ExecuteAsync(
            "UPDATE planned_task SET status = @s, diagnosis = @d WHERE id = @id;", ct,
            ("@s", targetState), ("@d", (object?)diagnosis ?? DBNull.Value), ("@id", task.Id))
            .ConfigureAwait(false);

        await ExecuteAsync("""
            INSERT INTO task_transition (id, task_id, from_state, to_state, evidence_refs, note, at_utc)
            VALUES (@id, @t, @from, @to, @ev, @note, @at);
            """, ct,
            ("@id", Guid.NewGuid().ToString("N")), ("@t", task.Id), ("@from", task.Status),
            ("@to", targetState), ("@ev", string.Join(',', evidence.Refs)),
            ("@note", (object?)evidence.Note ?? DBNull.Value), ("@at", Iso(_clock.UtcNow)))
            .ConfigureAwait(false);
    }

    private async Task<Plan> WritePlanAsync(
        string goalId, int revision, string rationale, IReadOnlyList<string> assumptions,
        IReadOnlyList<TaskRequest> tasks, CancellationToken ct)
    {
        var taskIds = new List<string>();
        var byTitle = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (TaskRequest request in tasks)
        {
            var id = Guid.NewGuid().ToString("N");
            byTitle[request.Title] = id;
            taskIds.Add(id);
        }

        var index = 0;
        foreach (TaskRequest request in tasks)
        {
            // Dependencies are given by title so a caller can describe a plan without inventing
            // identifiers; unknown titles are refused rather than silently dropped.
            var dependencies = request.Dependencies.Select(d =>
                byTitle.TryGetValue(d, out var id)
                    ? id
                    : throw new PlanningException($"Task '{request.Title}' depends on unknown '{d}'."))
                .ToList();

            await ExecuteAsync("""
                INSERT INTO planned_task
                    (id, goal_id, title, description, kind, status, dependencies, inputs_json,
                     expected_output_schema, risk, assigned_to, retry_policy, idempotency_key,
                     acceptance_tests, diagnosis)
                VALUES (@id, @g, @title, @desc, @kind, @status, @deps, @inputs, @schema, @risk,
                        @assignee, @retry, @idem, @accept, NULL);
                """, ct,
                ("@id", taskIds[index]), ("@g", goalId), ("@title", request.Title),
                ("@desc", request.Description), ("@kind", request.Kind),
                ("@status", dependencies.Count == 0 ? TaskState.Ready : TaskState.Draft),
                ("@deps", string.Join(',', dependencies)), ("@inputs", request.InputsJson),
                ("@schema", (object?)request.ExpectedOutputSchema ?? DBNull.Value),
                ("@risk", request.Risk), ("@assignee", request.AssignedTo),
                ("@retry", request.RetryPolicy),
                ("@idem", (object?)request.IdempotencyKey ?? DBNull.Value),
                ("@accept", string.Join(',', request.AcceptanceTests ?? []))).ConfigureAwait(false);

            index++;
        }

        var plan = new Plan(
            Guid.NewGuid().ToString("N"), goalId, revision, rationale, assumptions,
            taskIds, PlanStatus.Proposed);

        await ExecuteAsync("""
            INSERT INTO plan (id, goal_id, revision, rationale, assumptions, task_ids, status)
            VALUES (@id, @g, @rev, @rat, @assum, @tasks, @status);
            """, ct,
            ("@id", plan.Id), ("@g", goalId), ("@rev", revision), ("@rat", rationale),
            ("@assum", string.Join('\n', assumptions)), ("@tasks", string.Join(',', taskIds)),
            ("@status", plan.Status)).ConfigureAwait(false);

        return plan;
    }

    private Task SaveGoalAsync(Goal g, CancellationToken ct) =>
        ExecuteAsync("""
            INSERT INTO goal
                (id, title, outcome, owner_id, priority, status, constraints_json, success_criteria,
                 deadline_at_utc, budget_json, created_from_ref, approval_policy_id, blocked_reason)
            VALUES (@id, @t, @o, @owner, @p, @s, @c, @sc, @d, @b, @from, @policy, NULL);
            """, ct,
            ("@id", g.Id), ("@t", g.Title), ("@o", g.Outcome), ("@owner", g.OwnerId),
            ("@p", g.Priority), ("@s", g.Status), ("@c", g.ConstraintsJson),
            ("@sc", string.Join('\n', g.SuccessCriteria)),
            ("@d", (object?)g.DeadlineAtUtc ?? DBNull.Value), ("@b", g.BudgetJson),
            ("@from", (object?)g.CreatedFromRef ?? DBNull.Value),
            ("@policy", (object?)g.ApprovalPolicyId ?? DBNull.Value));

    private async Task ExecuteAsync(string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((var name, var value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private const string GoalSelect = """
        SELECT id, title, outcome, owner_id, priority, status, constraints_json, success_criteria,
               deadline_at_utc, budget_json, created_from_ref, approval_policy_id, blocked_reason
          FROM goal
        """;

    private const string TaskSelect = """
        SELECT id, goal_id, title, description, kind, status, dependencies, inputs_json,
               expected_output_schema, risk, assigned_to, retry_policy, idempotency_key,
               acceptance_tests, diagnosis
          FROM planned_task
        """;

    private static Goal ReadGoal(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5),
        r.GetString(6), r.GetString(7).Split('\n', StringSplitOptions.RemoveEmptyEntries),
        r.IsDBNull(8) ? null : r.GetString(8), r.GetString(9),
        r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11),
        r.IsDBNull(12) ? null : r.GetString(12));

    private static PlannedTask ReadTask(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        Split(r.GetString(6)), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
        r.GetString(9), r.GetString(10), r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12),
        Split(r.GetString(13)), r.IsDBNull(14) ? null : r.GetString(14));

    private static IReadOnlyList<string> Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries);

    private static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
