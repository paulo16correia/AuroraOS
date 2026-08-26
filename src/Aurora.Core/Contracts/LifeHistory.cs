namespace Aurora.Core.Contracts;

public static class EpisodeKind
{
    public const string Birth = "BIRTH";
    public const string Milestone = "MILESTONE";
    public const string FirstUse = "FIRST_USE";
    public const string Incident = "INCIDENT";
    public const string Learning = "LEARNING";
    public const string Transition = "TRANSITION";

    public static bool IsKnown(string kind) =>
        kind is Birth or Milestone or FirstUse or Incident or Learning or Transition;
}

public static class EpisodeStatus
{
    /// <summary>Proposed and not yet checked against its evidence. Never narrated.</summary>
    public const string Candidate = "CANDIDATE";

    public const string Verified = "VERIFIED";

    /// <summary>Withdrawn from the narrative. The revision trail survives.</summary>
    public const string Retracted = "RETRACTED";
}

public static class Significance
{
    public const string Low = "LOW";
    public const string Medium = "MEDIUM";
    public const string High = "HIGH";

    public static bool IsKnown(string significance) => significance is Low or Medium or High;
}

/// <summary>
/// One thing that happened to this instance, and what is said about it (RFC 038).
/// </summary>
/// <remarks>
/// <see cref="EvidenceRefs"/> and <see cref="NarrativeSummary"/> are different in kind and are
/// treated differently everywhere: the first is what the journal recorded and cannot be edited, the
/// second is a sentence somebody wrote about it and can. Rule 3 turns on that difference.
/// </remarks>
public sealed record LifeEpisode(
    string Id,
    string MindId,
    string Kind,
    string OccurredAtUtc,
    string? OccurredUntilUtc,
    string Title,
    string NarrativeSummary,
    IReadOnlyList<string> EvidenceRefs,
    string Significance,
    string Status,
    string SensitivityClass,
    string ProposedAtUtc,
    string? VerifiedAtUtc = null,
    string? RetractedReason = null,
    /// <summary>
    /// The genome in force when this happened (RFC 036 rule 3).
    /// </summary>
    /// <remarks>
    /// "The entire installation preserves reference to the effective Genome in Mind State **and
    /// Life History**." Mind State had it; this did not, so an episode could not be read against
    /// the version of Aurora that produced it — and an episode from an earlier genome is an
    /// episode of a slightly different entity.
    /// </remarks>
    string? EffectiveGenomeRef = null);

/// <summary>An audited change to what an episode says. The evidence is never among the changes.</summary>
public sealed record EpisodeRevision(
    string Id, string EpisodeId, string PreviousSummary, string NewSummary,
    string Actor, string Reason, string AtUtc);

public sealed record LifeHistory(
    string MindId,
    IReadOnlyList<string> EpisodeRefs,
    int NarrativeVersion,
    string LastCompiledAtUtc);

/// <summary>
/// One line of a narrative, and whether it is a record or a reading of one.
/// </summary>
/// <remarks>
/// Rule 2 asks the narrative to distinguish confirmed events from interpretative summaries, and the
/// only way to keep that true is to make them different things rather than different paragraphs. A
/// line with no <see cref="EvidenceRef"/> is somebody's reading, and says so.
/// </remarks>
public sealed record NarrativeLine(
    string Text,
    bool Confirmed,
    string? EvidenceRef,
    string? OccurredAtUtc);

/// <summary>
/// A narrative that carries its own sources.
/// </summary>
/// <remarks>
/// <see cref="Gaps"/> is not an apology. RFC 038's limit case says an unidentifiable "first error"
/// is answered by reporting insufficient evidence rather than by choosing an arbitrary episode, and
/// this is where that answer goes.
/// </remarks>
public sealed record CitedNarrative(
    string MindId,
    IReadOnlyList<NarrativeLine> Lines,
    IReadOnlyList<string> Gaps,
    int NarrativeVersion,
    string CompiledAtUtc);

public sealed class LifeHistoryException : Exception
{
    public LifeHistoryException(string message) : base(message)
    {
    }
}
