using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>What arrived, and how long it may take.</summary>
public sealed record CycleIngress(
    string WorkItemId, string IngressRef, string? McpSessionRef, string? DeadlineAtUtc = null);

/// <summary>The governed cycle every request passes through (RFC 021).</summary>
public interface ICognitiveCycle
{
    /// <summary>Starts a cycle at Perception.</summary>
    Task<CognitiveCycle> RunAsync(CycleIngress ingress, CancellationToken ct);

    /// <summary>
    /// Records a stage. Refuses to jump over one: an intervening stage must either have run or
    /// carry a recorded omission reason (RFC 021 rule 1).
    /// </summary>
    Task<CycleStageRecord> AdvanceAsync(
        string cycleId, string stage, IReadOnlyList<string> inputRefs,
        IReadOnlyList<string> outputRefs, string? decisionRef, CancellationToken ct);

    /// <summary>Records a stage as deliberately not run, with the reason.</summary>
    Task<CycleStageRecord> OmitAsync(string cycleId, string stage, string reason, CancellationToken ct);

    /// <summary>
    /// Marks that a capability effect occurred. Refused unless Policy has run and allowed it,
    /// with approval satisfied where required (rule 3).
    /// </summary>
    Task<CognitiveCycle> MarkExecutedAsync(
        string cycleId, bool policyAllowed, bool approvalSatisfied, CancellationToken ct);

    /// <summary>
    /// Completes the cycle. Refuses a result carrying persistent state or an execution result
    /// before Decision and Policy (rule 2), and refuses to close after an execution without
    /// Observation and Reflection (rule 5).
    /// </summary>
    Task<CycleResult> CompleteAsync(
        string cycleId, bool carriesPersistentStateOrExecution, string summary, CancellationToken ct);

    /// <summary>Parks the cycle for data, time, approval or availability (rule 4).</summary>
    Task<CognitiveCycle> WaitAsync(string cycleId, string reason, CancellationToken ct);

    /// <summary>Resumes a waiting cycle. Idempotent: resuming a running cycle changes nothing.</summary>
    Task<CognitiveCycle> ResumeAsync(string cycleId, string trigger, CancellationToken ct);

    /// <summary>Ends the cycle without a result, recording why.</summary>
    Task<CognitiveCycle> FailAsync(string cycleId, string reason, CancellationToken ct);

    Task<CognitiveCycle?> GetAsync(string cycleId, CancellationToken ct);

    Task<IReadOnlyList<CycleStageRecord>> StagesAsync(string cycleId, CancellationToken ct);
}
