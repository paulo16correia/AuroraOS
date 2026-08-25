using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// Internal deliberation, and the explanation it can offer (RFC 025).
/// </summary>
/// <remarks>
/// The interface is shaped by what it refuses to expose. There is no method that returns a trace:
/// the protected technical material can be written and can expire, and no caller can read it back.
/// Asking Aurora how it reached a conclusion returns a <see cref="Thought"/> — the reason, the
/// sources and the next effect — because that is an explanation, and a transcript of intermediate
/// reasoning is not.
/// </remarks>
public interface IDeliberationService
{
    /// <summary>
    /// Opens a deliberation against a live cycle and a deadline.
    /// </summary>
    /// <remarks>
    /// Both are required. Rule 1 forbids an ownerless global mental process, and something that
    /// belongs to no cycle and never has to finish is precisely that.
    /// </remarks>
    Task<DeliberationState> StartAsync(
        string cycleId, string question, DateTimeOffset deadline, CancellationToken ct);

    /// <summary>
    /// Moves it forward. Phases run in order and a step never goes back.
    /// </summary>
    Task<DeliberationState> AdvanceAsync(
        string deliberationId, string phase, DeliberationStep step, CancellationToken ct);

    /// <summary>
    /// Produces the explanation, from the state rather than from the trace.
    /// </summary>
    Task<Thought> SummariseAsync(
        string deliberationId, ThoughtRequest request, CancellationToken ct);

    /// <summary>
    /// Ends it, with how. An inconclusive deliberation must leave concrete questions behind.
    /// </summary>
    Task<DeliberationState> CloseAsync(
        string deliberationId, string disposition, CancellationToken ct);

    Task<DeliberationState?> GetAsync(string deliberationId, CancellationToken ct);

    Task<Thought?> ThoughtAsync(string thoughtId, CancellationToken ct);

    /// <summary>
    /// The explanations produced during one cycle, oldest first.
    /// </summary>
    /// <remarks>
    /// How a person asks "why did you do that". The answer is the reason, the sources and what
    /// happened next — never the working notes, which this cannot reach.
    /// </remarks>
    Task<IReadOnlyList<Thought>> ThoughtsForCycleAsync(string cycleId, CancellationToken ct);

    /// <summary>
    /// Whether the trace behind a decision is still there (RFC 025 limit case).
    /// </summary>
    /// <remarks>
    /// Answers yes or no and never the contents. A decision whose trace is gone stands only if its
    /// sources and policy are recoverable without it — this is how a caller finds out which case
    /// it is in.
    /// </remarks>
    Task<bool> TraceAvailableAsync(string deliberationId, CancellationToken ct);

    /// <summary>Discards traces past their retention, and closes deliberations past their deadline.</summary>
    Task<int> ExpireDueAsync(CancellationToken ct);
}
