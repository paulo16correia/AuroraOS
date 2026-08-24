using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// Owns the macro state of an instance (RFC 039). The Kernel is the only actor that may move it.
/// </summary>
public interface IInstanceLifecycle
{
    /// <summary>Reads the current state, creating the instance in `CREATED` when it is new.</summary>
    Task<InstanceLifecycle> GetOrCreateAsync(string instanceId, CancellationToken ct);

    /// <summary>
    /// Moves the instance, enforcing the state machine and the mandatory rules. Refuses rather than
    /// forcing: a lifecycle that can be pushed into any state records nothing worth reading.
    /// </summary>
    Task<TransitionResult> TransitionAsync(
        string instanceId, string targetState, string actor, string reason,
        bool emergency = false, CancellationToken ct = default);

    /// <summary>Records a proposal from the Mind. It never changes the state by itself (rule 4).</summary>
    Task<LifecycleProposal> ProposeAsync(
        string instanceId, string targetState, string reason, CancellationToken ct);

    /// <summary>The work required before shutdown, including whether a verified snapshot exists.</summary>
    Task<ShutdownPlan> PrepareShutdownAsync(string instanceId, CancellationToken ct);

    /// <summary>Returns a paused or waiting instance to `READY`.</summary>
    Task<TransitionResult> ResumeAsync(string instanceId, string reason, CancellationToken ct);

    /// <summary>Records the snapshot that makes a clean stop possible (rule 1).</summary>
    Task SetVerifiedSnapshotAsync(string instanceId, string snapshotRef, CancellationToken ct);

    /// <summary>Marks work that must be reconciled before the instance is `READY` again.</summary>
    Task SetPendingActionsAsync(string instanceId, IReadOnlyList<string> actionRefs, CancellationToken ct);
}
