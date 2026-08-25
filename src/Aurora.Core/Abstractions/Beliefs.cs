using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// Reviewable generalisations that guide attention and are never mistaken for facts (RFC 028).
/// </summary>
/// <remarks>
/// The surface is shaped around two refusals. A belief cannot be proposed on the model's word
/// alone, and support for a high-risk purpose always comes back saying it is not enough on its own.
/// Both are returned rather than thrown where a caller could reasonably continue — the point is not
/// to stop Aurora using patterns, it is to stop a pattern being mistaken for a reason.
/// </remarks>
public interface IBeliefSystem
{
    /// <summary>
    /// Proposes a belief. Refused without evidence that is not just the model's own output.
    /// </summary>
    Task<Belief> ProposeAsync(
        BeliefCandidate candidate, IReadOnlyList<string> evidenceRefs, CancellationToken ct);

    /// <summary>
    /// Applies an observation, up or down, and keeps the update.
    /// </summary>
    /// <remarks>
    /// A prediction that failed gets counter-evidence attached and is re-evaluated. It is never
    /// silently erased: the record of having believed something wrong is the useful part.
    /// </remarks>
    Task<BeliefUpdate> ObserveAsync(
        string beliefId, string observationRef, double deltaConfidence, string reason, CancellationToken ct);

    /// <summary>
    /// Offers beliefs as support for a purpose, and says whether they can carry it alone.
    /// </summary>
    Task<BeliefSupport> SupportAsync(
        string subjectRef, string purpose, MemoryAccessContext access, CancellationToken ct);

    /// <summary>
    /// Records evidence against. The belief becomes CHALLENGED rather than quietly averaged down.
    /// </summary>
    /// <remarks>
    /// RFC 028's limit case is explicit that contradiction is answered by narrowing or separating
    /// scope, not by splitting the difference. Averaging turns two incompatible observations into
    /// one lukewarm claim that describes neither.
    /// </remarks>
    Task<Belief> ChallengeAsync(
        string beliefId, string evidenceRef, string reason, CancellationToken ct);

    /// <summary>
    /// Narrows a challenged belief to where it still holds, reactivating it.
    /// </summary>
    Task<Belief> NarrowAsync(string beliefId, string scopeJson, string reason, CancellationToken ct);

    Task<Belief> RetractAsync(string beliefId, string reason, CancellationToken ct);

    /// <summary>Ages beliefs nobody has confirmed, and expires those past review (rule 3).</summary>
    Task<int> ReviewDueAsync(CancellationToken ct);

    Task<Belief?> GetAsync(string beliefId, CancellationToken ct);

    Task<IReadOnlyList<BeliefUpdate>> UpdatesAsync(string beliefId, CancellationToken ct);
}
