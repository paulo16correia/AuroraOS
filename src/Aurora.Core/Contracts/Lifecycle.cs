namespace Aurora.Core.Contracts;

/// <summary>Macro states of an Aurora instance (RFC 039).</summary>
public static class InstanceState
{
    public const string Created = "CREATED";
    public const string Bootstrapping = "BOOTSTRAPPING";
    public const string Recovering = "RECOVERING";
    public const string Ready = "READY";
    public const string Deliberating = "DELIBERATING";
    public const string Executing = "EXECUTING";
    public const string Maintaining = "MAINTAINING";
    public const string Waiting = "WAITING";
    public const string Paused = "PAUSED";
    public const string BackingUp = "BACKING_UP";
    public const string Updating = "UPDATING";
    public const string ShuttingDown = "SHUTTING_DOWN";
    public const string Stopped = "STOPPED";
    public const string Retired = "RETIRED";

    /// <summary>
    /// Whether the instance may start new effects. RFC 039 rule 3: <c>PAUSED</c> prevents them, and
    /// so does everything from shutdown onwards.
    /// </summary>
    public static bool AllowsNewEffects(string state) =>
        state is Ready or Deliberating or Executing or Maintaining;

    /// <summary>
    /// States that must drain idempotent work and mark incomplete external calls for
    /// reconciliation before they are entered (RFC 039 rule 2).
    /// </summary>
    public static bool RequiresDrain(string state) => state is BackingUp or Updating;
}

/// <summary>
/// Who is asking for a transition. RFC 039 rule 4: only the Kernel performs them; the Mind may
/// propose and never change the lifecycle directly.
/// </summary>
public static class TransitionActor
{
    public const string Kernel = "KERNEL";
    public const string Mind = "MIND";
}

/// <summary>The operational existence of one instance (RFC 039, RFC 040).</summary>
public sealed record InstanceLifecycle(
    string InstanceId,
    string State,
    string EnteredAtUtc,
    string? Reason,
    IReadOnlyList<string> ActiveCycleRefs,
    IReadOnlyList<string> PendingActionRefs,
    string? LastVerifiedSnapshotRef,
    long Version);

/// <summary>Why a transition was refused.</summary>
public enum TransitionRefusal
{
    None,
    NotFound,

    /// <summary>The edge does not exist in the RFC 039 state machine.</summary>
    IllegalTransition,

    /// <summary>Only the Kernel may transition (rule 4).</summary>
    NotAuthorised,

    /// <summary>`STOPPED` needs a verified snapshot or an audited emergency reason (rule 1).</summary>
    StopWithoutSnapshotOrEmergency,

    /// <summary>`BACKING_UP` and `UPDATING` must drain first (rule 2).</summary>
    DrainRequired,
}

public sealed record TransitionResult(
    bool Ok, InstanceLifecycle? Lifecycle, TransitionRefusal Refusal = TransitionRefusal.None);

/// <summary>A proposal from the Mind, which the Kernel may or may not act on (rule 4).</summary>
public sealed record LifecycleProposal(string InstanceId, string TargetState, string Reason, string ProposedAtUtc);

/// <summary>Ordered work the Kernel performs before entering `SHUTTING_DOWN` (RFC 039).</summary>
public sealed record ShutdownPlan(
    string InstanceId,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> PendingActionRefs,
    bool HasVerifiedSnapshot);
