namespace Aurora.Core.Contracts;

/// <summary>
/// The mandatory stages, in the order RFC 021 lays them out.
/// </summary>
/// <remarks>
/// The order is data rather than control flow so that "did this cycle skip a stage?" is a question
/// with an answer, checkable after the fact from the record alone.
/// </remarks>
public static class CycleStage
{
    public const string Perception = "PERCEPTION";
    public const string Attention = "ATTENTION";
    public const string WorkingMemory = "WORKING_MEMORY";
    public const string Memory = "MEMORY";
    public const string WorldModel = "WORLD_MODEL";
    public const string Planner = "PLANNER";
    public const string Decision = "DECISION";
    public const string Policy = "POLICY";
    public const string Capabilities = "CAPABILITIES";
    public const string Executor = "EXECUTOR";
    public const string Observation = "OBSERVATION";
    public const string Reflection = "REFLECTION";
    public const string Learning = "LEARNING";

    public static readonly IReadOnlyList<string> Order =
    [
        Perception, Attention, WorkingMemory, Memory, WorldModel, Planner,
        Decision, Policy, Capabilities, Executor, Observation, Reflection, Learning,
    ];

    public static int IndexOf(string stage) => Order.ToList().IndexOf(stage);
}

public static class CycleStatus
{
    public const string Running = "RUNNING";
    public const string Waiting = "WAITING";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";
}

public static class StageStatus
{
    public const string Done = "DONE";

    /// <summary>Not run, with a recorded reason. RFC 021 rule 1 allows this and nothing less.</summary>
    public const string Omitted = "OMITTED";

    public const string Failed = "FAILED";
}

public sealed record CognitiveCycle(
    string Id,
    string WorkItemId,
    string Stage,
    string Status,
    string IngressRef,
    string? McpSessionRef,
    string StartedAtUtc,
    string? DeadlineAtUtc,
    string? CompletedAtUtc,
    bool Executed);

public sealed record CycleStageRecord(
    string CycleId,
    string Stage,
    IReadOnlyList<string> InputRefs,
    IReadOnlyList<string> OutputRefs,
    string? DecisionRef,
    string StartedAtUtc,
    string? EndedAtUtc,
    string Status,
    string? Note);

/// <summary>What the cycle hands back to the MCP client.</summary>
/// <remarks>
/// <see cref="CarriesPersistentStateOrExecution"/> is declared by the caller and is what rule 2
/// keys on: such a result may only be produced after Decision and Policy have run.
/// </remarks>
public sealed record CycleResult(
    string CycleId, string Status, bool CarriesPersistentStateOrExecution, string Summary);

public sealed class CognitiveCycleException : Exception
{
    public CognitiveCycleException(string message) : base(message)
    {
    }
}
