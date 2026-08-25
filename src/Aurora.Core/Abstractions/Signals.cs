using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// Turns facts into priorities (RFC 030).
/// </summary>
/// <remarks>
/// The nervous system, not the hands. Nothing on this interface names a capability, a tool or an
/// approval, and that is the point of rule 2: urgency changes what Aurora looks at and in what
/// order, and never what it is allowed to do. A CRITICAL signal reaches the front of the queue and
/// then waits for the same decision and the same permission as everything else.
/// </remarks>
public interface ISignalService
{
    /// <summary>
    /// Raises a signal about a fact that is already on record.
    /// </summary>
    /// <remarks>
    /// Rule 1: the source must be verifiable. A signal whose source event does not exist is
    /// refused, because otherwise the classifier could invent the urgency and the evidence together.
    /// </remarks>
    Task<Signal> EmitAsync(
        string sourceEventRef, SignalClassification classification, SignalPolicy policy, CancellationToken ct);

    /// <summary>
    /// Decides how much attention a signal gets, parking any cycle it interrupts.
    /// </summary>
    /// <remarks>
    /// Rule 3: interrupting takes a policy threshold, and the interrupted cycle is preserved for
    /// recovery rather than dropped — otherwise an urgent alert would quietly destroy whatever
    /// Aurora was in the middle of.
    /// </remarks>
    Task<RouteDecision> RouteAsync(
        string signalId, string? cycleInProgressId, SignalPolicy policy, CancellationToken ct);

    /// <summary>Closes a signal against what actually resolved it.</summary>
    Task<Signal> AcknowledgeAsync(string signalId, string resolutionRef, CancellationToken ct);

    /// <summary>
    /// Expires signals past their lifetime. Rule 4: a signal ends, one way or another.
    /// </summary>
    Task<int> ExpireDueAsync(CancellationToken ct);

    Task<Signal?> GetAsync(string signalId, CancellationToken ct);

    /// <summary>The signals still awaiting attention, most pressing first.</summary>
    Task<IReadOnlyList<Signal>> PendingAsync(CancellationToken ct);
}
