using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// A verifiable narrative of what happened to this instance (RFC 038).
/// </summary>
/// <remarks>
/// It does not replace the audit journal and does not turn inferences into autobiography. A
/// collection of memories is not automatically a narrative identity — every episode is proposed
/// against auditable evidence, checked before it is ever narrated, and marked as a record or as a
/// reading of one.
/// </remarks>
public interface ILifeHistory
{
    /// <summary>
    /// Proposes an episode. Refused without evidence that resolves in the audit journal (rule 1).
    /// </summary>
    Task<LifeEpisode> ProposeAsync(LifeEpisode candidate, CancellationToken ct);

    /// <summary>
    /// Checks an episode against its evidence. Only a verified one is ever narrated.
    /// </summary>
    Task<LifeEpisode> VerifyAsync(string episodeId, CancellationToken ct);

    /// <summary>
    /// Compiles the narrative for an audience, with a source on every confirmed line.
    /// </summary>
    /// <remarks>
    /// The audience decides what is visible: rule 4 keeps sensitive material out of a narrative
    /// unless the policy covers it.
    /// </remarks>
    Task<CitedNarrative> NarrateAsync(
        string mindId, MemoryAccessContext audience, CancellationToken ct);

    /// <summary>
    /// Answers a question about the instance's past, or says the evidence does not support one.
    /// </summary>
    /// <remarks>
    /// The limit case, given its own method because it is the interesting behaviour: asked when it
    /// first made a mistake with nothing to ground the answer, Aurora reports insufficient evidence
    /// rather than choosing an episode that will do.
    /// </remarks>
    Task<CitedNarrative> AnswerAsync(
        string mindId, string kind, MemoryAccessContext audience, CancellationToken ct);

    /// <summary>
    /// Rewrites what an episode says. Never touches its evidence or the journal (rule 3).
    /// </summary>
    Task<LifeEpisode> CorrectAsync(
        string episodeId, string summary, string actor, string reason, CancellationToken ct);

    /// <summary>Removes an episode from the narrative, keeping the trail of it having been there.</summary>
    Task<LifeEpisode> RetractAsync(string episodeId, string reason, string actor, CancellationToken ct);

    Task<IReadOnlyList<EpisodeRevision>> RevisionsAsync(string episodeId, CancellationToken ct);

    Task<LifeEpisode?> GetAsync(string episodeId, CancellationToken ct);
}
