using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.LifeHistory;

/// <summary>
/// A verifiable narrative of what happened to this instance (RFC 038).
/// </summary>
/// <remarks>
/// The line this class exists to hold: a collection of memories is not automatically a narrative
/// identity. Every episode is proposed against evidence that must resolve in the audit journal,
/// checked before it is ever narrated, and rendered as a record or as a reading of one — never as
/// a sentence that sounds like both.
/// </remarks>
public sealed class SqliteLifeHistory : ILifeHistory
{
    private readonly SqliteConnectionFactory _factory;
    private readonly IAuditStore _audit;
    private readonly IEventBus _bus;
    private readonly IClock _clock;

    public SqliteLifeHistory(SqliteConnectionFactory factory, IAuditStore audit, IClock clock, IEventBus bus)
    {
        _factory = factory;
        _audit = audit;
        _clock = clock;
        _bus = bus;
    }

    public async Task<LifeEpisode> ProposeAsync(LifeEpisode candidate, CancellationToken ct)
    {
        if (!EpisodeKind.IsKnown(candidate.Kind) || !Significance.IsKnown(candidate.Significance))
        {
            throw new LifeHistoryException("An episode needs a known kind and significance.");
        }

        // Rule 1: auditable evidence and a date. Without them this is not an episode, it is a
        // sentence about the past — which is the thing RFC 038 is written to keep out.
        if (candidate.EvidenceRefs.Count == 0)
        {
            throw new LifeHistoryException("An episode references the evidence it happened.");
        }

        if (string.IsNullOrWhiteSpace(candidate.OccurredAtUtc))
        {
            throw new LifeHistoryException("An episode is dated.");
        }

        var episode = candidate with
        {
            Id = Guid.NewGuid().ToString("N"),

            // CANDIDATE, always. Proposing is not remembering, and nothing reaches the narrative
            // without having been checked against what actually happened.
            Status = EpisodeStatus.Candidate,
            ProposedAtUtc = Iso(_clock.UtcNow),
            VerifiedAtUtc = null,
            RetractedReason = null,
        };

        await ExecuteAsync("""
            INSERT INTO life_episode
                (id, mind_id, kind, occurred_at_utc, occurred_until_utc, title, narrative_summary,
                 evidence_refs, significance, status, sensitivity_class, proposed_at_utc,
                 verified_at_utc, retracted_reason, effective_genome_ref)
            VALUES (@id, @mind, @kind, @at, @until, @title, @summary, @evidence, @significance,
                    @status, @sensitivity, @proposed, NULL, NULL, @genome);
            """, ct,
            ("@genome", (object?)episode.EffectiveGenomeRef ?? DBNull.Value),
            ("@id", episode.Id), ("@mind", episode.MindId), ("@kind", episode.Kind),
            ("@at", episode.OccurredAtUtc),
            ("@until", (object?)episode.OccurredUntilUtc ?? DBNull.Value),
            ("@title", episode.Title), ("@summary", episode.NarrativeSummary),
            ("@evidence", string.Join('\n', episode.EvidenceRefs)),
            ("@significance", episode.Significance), ("@status", episode.Status),
            ("@sensitivity", episode.SensitivityClass), ("@proposed", episode.ProposedAtUtc))
            .ConfigureAwait(false);

        return episode;
    }

    public async Task<LifeEpisode> VerifyAsync(string episodeId, CancellationToken ct)
    {
        LifeEpisode episode = await RequireAsync(episodeId, ct).ConfigureAwait(false);

        if (episode.Status == EpisodeStatus.Retracted)
        {
            throw new LifeHistoryException("A retracted episode is not verified; propose a new one.");
        }

        // Every reference is looked up. An episode whose evidence does not resolve is a story, and
        // the difference between a story and a history is exactly this check.
        IReadOnlyList<string> missing =
            await UnresolvedAsync(episode.EvidenceRefs, ct).ConfigureAwait(false);

        if (missing.Count > 0)
        {
            throw new LifeHistoryException(
                $"This evidence is not in the audit journal: {string.Join(", ", missing)}.");
        }

        var verifiedAt = Iso(_clock.UtcNow);

        await ExecuteAsync(
            "UPDATE life_episode SET status = @verified, verified_at_utc = @at WHERE id = @id;", ct,
            ("@verified", EpisodeStatus.Verified), ("@at", verifiedAt), ("@id", episodeId))
            .ConfigureAwait(false);

        // The narrative gained something. The title and the summary stay behind: what a person may
        // be told about Aurora's past is decided when they ask, not when it is recorded.
        await _bus.PublishAsync(
            new OutboxWrite(
                EventCatalogue.LifeEpisodeVerified, 1, EventCatalogue.Producers.LifeHistory,
                Guid.NewGuid().ToString("N"), Sensitivity.Private,
                AggregateRef: $"episode/{episodeId}",
                PayloadJson: AuroraJson.Serialize(
                    new { episode_id = episodeId, kind = episode.Kind }),
                IdempotencyKey: $"episode-verified:{episodeId}"),
            ct).ConfigureAwait(false);

        return episode with { Status = EpisodeStatus.Verified, VerifiedAtUtc = verifiedAt };
    }

    public async Task<CitedNarrative> NarrateAsync(
        string mindId, MemoryAccessContext audience, CancellationToken ct)
    {
        IReadOnlyList<LifeEpisode> episodes = await VerifiedAsync(mindId, ct).ConfigureAwait(false);

        var lines = new List<NarrativeLine>();
        var gaps = new List<string>();

        // Rule 4: sensitive material stays out unless the audience's ceiling covers it. Withheld
        // rather than paraphrased — a redacted episode still discloses that something happened.
        var withheld = episodes
            .Count(e => Sensitivity.Rank(e.SensitivityClass) > Sensitivity.Rank(audience.MaxSensitivity));

        if (withheld > 0)
        {
            gaps.Add($"{withheld} episode(s) are above what this audience may be told");
        }

        foreach (LifeEpisode episode in episodes
                     .Where(e => Sensitivity.Rank(e.SensitivityClass) <= Sensitivity.Rank(audience.MaxSensitivity))
                     .OrderBy(e => e.OccurredAtUtc, StringComparer.Ordinal))
        {
            // Rule 2, and it is why these are two lines rather than one paragraph. The first is
            // what the journal recorded; the second is what somebody made of it, and a reader can
            // tell which is which without being told.
            lines.Add(new NarrativeLine(
                episode.Title, Confirmed: true, episode.EvidenceRefs[0], episode.OccurredAtUtc));

            if (!string.IsNullOrWhiteSpace(episode.NarrativeSummary))
            {
                lines.Add(new NarrativeLine(
                    episode.NarrativeSummary, Confirmed: false, null, episode.OccurredAtUtc));
            }
        }

        if (lines.Count == 0)
        {
            gaps.Add("nothing has been verified about this instance yet");
        }

        return new CitedNarrative(
            mindId, lines, gaps, NarrativeVersion: episodes.Count, Iso(_clock.UtcNow));
    }

    /// <summary>
    /// Answers a question about the past, or reports that the evidence does not support one.
    /// </summary>
    /// <remarks>
    /// RFC 038's limit case, and the behaviour worth having: asked when it first made a mistake
    /// with nothing to ground the answer, Aurora says there is not enough evidence rather than
    /// picking the episode that best fits the question. An arbitrary answer to a question about
    /// one's own past is not a small error — it is the beginning of an invented autobiography.
    /// </remarks>
    public async Task<CitedNarrative> AnswerAsync(
        string mindId, string kind, MemoryAccessContext audience, CancellationToken ct)
    {
        if (!EpisodeKind.IsKnown(kind))
        {
            throw new LifeHistoryException($"Unknown episode kind '{kind}'.");
        }

        IReadOnlyList<LifeEpisode> matching = (await VerifiedAsync(mindId, ct).ConfigureAwait(false))
            .Where(e => e.Kind == kind
                     && Sensitivity.Rank(e.SensitivityClass) <= Sensitivity.Rank(audience.MaxSensitivity))
            .OrderBy(e => e.OccurredAtUtc, StringComparer.Ordinal)
            .ToList();

        if (matching.Count == 0)
        {
            return new CitedNarrative(
                mindId, [],
                [$"nothing of kind {kind} has been verified; there is not enough evidence to answer"],
                NarrativeVersion: 0, Iso(_clock.UtcNow));
        }

        LifeEpisode first = matching[0];

        return new CitedNarrative(
            mindId,
            [
                new NarrativeLine(first.Title, true, first.EvidenceRefs[0], first.OccurredAtUtc),
                new NarrativeLine(first.NarrativeSummary, false, null, first.OccurredAtUtc),
            ],
            Gaps: [], matching.Count, Iso(_clock.UtcNow));
    }

    public async Task<LifeEpisode> CorrectAsync(
        string episodeId, string summary, string actor, string reason, CancellationToken ct)
    {
        LifeEpisode episode = await RequireAsync(episodeId, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(reason))
        {
            throw new LifeHistoryException("A correction records who made it and why.");
        }

        // Rule 3: the text changes and the evidence does not. The audit journal is untouched by
        // construction — nothing in this method can reach it, which is the strongest form of the
        // guarantee the rule asks for.
        await ExecuteAsync(
            "UPDATE life_episode SET narrative_summary = @summary WHERE id = @id;", ct,
            ("@summary", summary), ("@id", episodeId)).ConfigureAwait(false);

        await ExecuteAsync("""
            INSERT INTO episode_revision
                (id, episode_id, previous_summary, new_summary, actor, reason, at_utc)
            VALUES (@id, @episode, @previous, @new, @actor, @reason, @at);
            """, ct,
            ("@id", Guid.NewGuid().ToString("N")), ("@episode", episodeId),
            ("@previous", episode.NarrativeSummary), ("@new", summary),
            ("@actor", actor), ("@reason", reason), ("@at", Iso(_clock.UtcNow)))
            .ConfigureAwait(false);

        return episode with { NarrativeSummary = summary };
    }

    public async Task<LifeEpisode> RetractAsync(
        string episodeId, string reason, string actor, CancellationToken ct)
    {
        LifeEpisode episode = await RequireAsync(episodeId, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new LifeHistoryException("A retraction says why.");
        }

        // Removed from the narrative, kept on record. Rule and limit case both: the trail of having
        // believed something about oneself is part of the history even when the episode is not.
        await ExecuteAsync(
            "UPDATE life_episode SET status = @retracted, retracted_reason = @reason WHERE id = @id;",
            ct,
            ("@retracted", EpisodeStatus.Retracted), ("@reason", $"{reason} (by {actor})"),
            ("@id", episodeId)).ConfigureAwait(false);

        return episode with
        {
            Status = EpisodeStatus.Retracted, RetractedReason = $"{reason} (by {actor})",
        };
    }

    public async Task<IReadOnlyList<EpisodeRevision>> RevisionsAsync(
        string episodeId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, episode_id, previous_summary, new_summary, actor, reason, at_utc
              FROM episode_revision WHERE episode_id = @id ORDER BY at_utc;
            """;
        command.Parameters.AddWithValue("@id", episodeId);

        var revisions = new List<EpisodeRevision>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            revisions.Add(new EpisodeRevision(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6)));
        }

        return revisions;
    }

    public async Task<LifeEpisode?> GetAsync(string episodeId, CancellationToken ct)
    {
        IReadOnlyList<LifeEpisode> found = await ReadAsync(
            $"{Select} WHERE id = @id;", ct, ("@id", episodeId)).ConfigureAwait(false);

        return found.Count == 0 ? null : found[0];
    }

    // ---- plumbing ----

    /// <summary>
    /// Which of these references are not in the audit journal.
    /// </summary>
    /// <remarks>
    /// Walked in pages rather than read whole: the journal is the largest table Aurora keeps, and
    /// verification is not a reason to load all of it.
    /// </remarks>
    private async Task<IReadOnlyList<string>> UnresolvedAsync(
        IReadOnlyList<string> refs, CancellationToken ct)
    {
        var outstanding = new HashSet<string>(refs, StringComparer.Ordinal);
        long cursor = 0;

        while (outstanding.Count > 0)
        {
            IReadOnlyList<AuditRecordView> page =
                await _audit.QueryAsync(cursor, 200, ct).ConfigureAwait(false);

            if (page.Count == 0)
            {
                break;
            }

            foreach (AuditRecordView record in page)
            {
                outstanding.Remove(record.RecordId);
            }

            cursor = page[^1].Sequence;
        }

        return outstanding.ToList();
    }

    private Task<IReadOnlyList<LifeEpisode>> VerifiedAsync(string mindId, CancellationToken ct) =>
        ReadAsync(
            $"{Select} WHERE mind_id = @mind AND status = @verified ORDER BY occurred_at_utc;", ct,
            ("@mind", mindId), ("@verified", EpisodeStatus.Verified));

    private async Task<LifeEpisode> RequireAsync(string episodeId, CancellationToken ct) =>
        await GetAsync(episodeId, ct).ConfigureAwait(false)
        ?? throw new LifeHistoryException("Unknown episode.");

    private const string Select = """
        SELECT id, mind_id, kind, occurred_at_utc, occurred_until_utc, title, narrative_summary,
               evidence_refs, significance, status, sensitivity_class, proposed_at_utc,
               verified_at_utc, retracted_reason, effective_genome_ref
          FROM life_episode
        """;

    private async Task<IReadOnlyList<LifeEpisode>> ReadAsync(
        string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((var name, var value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var episodes = new List<LifeEpisode>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            episodes.Add(new LifeEpisode(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5), reader.GetString(6), Lines(reader.GetString(7)),
                reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14)));
        }

        return episodes;
    }

    private async Task ExecuteAsync(
        string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((var name, var value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> Lines(string value) =>
        value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
