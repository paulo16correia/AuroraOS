using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>One turn of a local conversation.</summary>
public sealed record PilotRequest(string ConversationRef, string Utterance, Principal Principal);

/// <summary>
/// What the slice produced, by reference.
/// </summary>
/// <remarks>
/// Every identifier here points at a record that outlives the process. The slice is only finished
/// if a fresh set of services over the same database can find all of them.
/// </remarks>
public sealed record PilotOutcome(
    string CycleId,
    string DecisionId,
    string ActionId,
    string ObservationId,
    string ReflectionId,
    string ResponseSummary,
    IReadOnlyList<string> AuditRefs,
    IReadOnlyList<string> StagesRun,
    IReadOnlyList<string> StagesOmitted);

/// <summary>
/// The low-risk pilot: the first vertical slice of the frozen implementation order.
/// </summary>
/// <remarks>
/// A local conversation opens a cycle, publishes an event, attends, frames, recalls, resolves,
/// decides to respond, passes policy, records the response as an observed action, reflects, and
/// closes — using no external tool. It is the smallest thing that exercises the whole governed
/// path, which is exactly why the implementation order puts it before any connector.
/// </remarks>
public interface IPilotApplication
{
    Task<PilotOutcome> RespondAsync(PilotRequest request, CancellationToken ct);

    /// <summary>Reads back a finished turn, so "survives restart" is checkable rather than asserted.</summary>
    Task<PilotOutcome?> RecallAsync(string cycleId, CancellationToken ct);
}
