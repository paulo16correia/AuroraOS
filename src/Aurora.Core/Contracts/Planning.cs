namespace Aurora.Core.Contracts;

public static class GoalStatus
{
    public const string Draft = "DRAFT";
    public const string Active = "ACTIVE";

    /// <summary>Incompatible with policy. It stays here with its reason, never decomposed around.</summary>
    public const string Blocked = "BLOCKED";

    public const string Paused = "PAUSED";
    public const string Completed = "COMPLETED";
    public const string Cancelled = "CANCELLED";
    public const string Failed = "FAILED";
}

public static class TaskKind
{
    public const string Research = "RESEARCH";
    public const string Decision = "DECISION";
    public const string Tool = "TOOL";
    public const string Human = "HUMAN";
    public const string Check = "CHECK";
}

public static class TaskState
{
    public const string Draft = "DRAFT";
    public const string Ready = "READY";
    public const string Running = "RUNNING";
    public const string WaitingInput = "WAITING_INPUT";
    public const string WaitingApproval = "WAITING_APPROVAL";
    public const string Succeeded = "SUCCEEDED";
    public const string Failed = "FAILED";
    public const string Skipped = "SKIPPED";
    public const string Cancelled = "CANCELLED";

    /// <summary>Nothing follows these.</summary>
    public static bool IsTerminal(string state) =>
        state is Succeeded or Failed or Skipped or Cancelled;

    /// <summary>
    /// The state machine RFC 05 rule 2 requires. Written out rather than inferred, so an illegal
    /// transition is a lookup failure and not an oversight.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Allowed =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [Draft] = [Ready, Cancelled, Skipped],
            [Ready] = [Running, WaitingInput, WaitingApproval, Cancelled, Skipped, Draft],
            [Running] = [Succeeded, Failed, WaitingInput, WaitingApproval, Cancelled],
            [WaitingInput] = [Ready, Cancelled, Skipped],
            [WaitingApproval] = [Ready, Running, Cancelled, Skipped],
            [Succeeded] = [],
            [Failed] = [Ready, Cancelled],
            [Skipped] = [],
            [Cancelled] = [],
        };
}

public static class PlanStatus
{
    public const string Proposed = "PROPOSED";
    public const string Approved = "APPROVED";
    public const string Active = "ACTIVE";
    public const string Superseded = "SUPERSEDED";
    public const string Closed = "CLOSED";
}

public static class Assignee
{
    public const string Aurora = "AURORA";
    public const string Human = "HUMAN";
}

public sealed record Goal(
    string Id,
    string Title,
    string Outcome,
    string OwnerId,
    int Priority,
    string Status,
    string ConstraintsJson,
    IReadOnlyList<string> SuccessCriteria,
    string? DeadlineAtUtc,
    string BudgetJson,
    string? CreatedFromRef,
    string? ApprovalPolicyId,
    string? BlockedReason = null);

public sealed record PlannedTask(
    string Id,
    string GoalId,
    string Title,
    string Description,
    string Kind,
    string Status,
    IReadOnlyList<string> Dependencies,
    string InputsJson,
    string? ExpectedOutputSchema,
    string Risk,
    string AssignedTo,
    string RetryPolicy,
    string? IdempotencyKey,
    IReadOnlyList<string> AcceptanceTests,
    string? Diagnosis = null);

public sealed record Plan(
    string Id,
    string GoalId,
    int Revision,
    string Rationale,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> TaskIds,
    string Status);

/// <summary>What the caller asks for. Missing outcome or criteria produces discovery, not guesswork.</summary>
public sealed record GoalRequest(
    string Title,
    string Outcome,
    string OwnerId,
    IReadOnlyList<string> SuccessCriteria,
    IReadOnlyList<string> Assumptions,
    int Priority = 3,
    string ConstraintsJson = "{}",
    string BudgetJson = "{}",
    string? DeadlineAtUtc = null,
    string? CreatedFromRef = null,
    string? ApprovalPolicyId = null);

/// <summary>A task the caller wants in the plan.</summary>
public sealed record TaskRequest(
    string Title,
    string Description,
    string Kind,
    IReadOnlyList<string> Dependencies,
    string Risk,
    string AssignedTo = Assignee.Aurora,
    string InputsJson = "{}",
    string? ExpectedOutputSchema = null,
    string RetryPolicy = "none",
    string? IdempotencyKey = null,
    IReadOnlyList<string>? AcceptanceTests = null);

/// <summary>Evidence for a task transition (RFC 05 rule 2).</summary>
public sealed record TransitionEvidence(
    IReadOnlyList<string> Refs,
    IReadOnlyList<AcceptanceResult>? AcceptanceResults = null,
    string? Note = null,
    /// <summary>An explicit rule permitting a run with unmet dependencies (rule 3's exception).</summary>
    string? DependencyOverrideRule = null);

public sealed record AcceptanceResult(string Test, bool Passed, string? Detail = null);

public sealed record PlanRevision(Plan Previous, Plan Current, string Trigger);

/// <summary>What to do about a goal whose deadline has passed.</summary>
public static class DeadlineAction
{
    public const string Notify = "NOTIFY";
    public const string Pause = "PAUSE";
    public const string Continue = "CONTINUE";
}

public sealed record OverdueGoal(string GoalId, string DeadlineAtUtc, string ActionTaken);

public sealed class PlanningException : Exception
{
    public PlanningException(string message) : base(message)
    {
    }
}
