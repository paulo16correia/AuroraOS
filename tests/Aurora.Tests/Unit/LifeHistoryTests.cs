using System.Globalization;
using Aurora.Adapters.LifeHistory;
using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Life history (RFC 038): a narrative that carries its sources, and admits its gaps.
/// </summary>
/// <remarks>
/// The line under test throughout: a collection of memories is not automatically a narrative
/// identity, and an inference is not an autobiography.
/// </remarks>
public sealed class LifeHistoryTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Mind = "mind/local";

    private static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record World(SqliteLifeHistory History, SqliteAuditStore Audit, TestClock Clock);

    private static World Build(SqliteTestDb db, string now = "2026-01-15T09:00:00+00:00")
    {
        var clock = new TestClock(At(now));
        var audit = new SqliteAuditStore(
            db.Factory, clock, new byte[32],
            new AuditAnchorFile(Path.Combine(Path.GetTempPath(), $"a-{Guid.NewGuid():N}")));

        return new World(new SqliteLifeHistory(db.Factory, audit, clock, TestBus.Over(db.Factory, clock)), audit, clock);
    }

    /// <summary>Writes a real audit record and returns its id, so evidence resolves.</summary>
    private static async Task<string> RecordAsync(World world, string action)
    {
        await world.Audit.AppendAsync(
            new AuditEntry(
                "c1", "u1", action, $"hash-{Guid.NewGuid():N}", "completed",
                Risk: "Low", Via: "explicit", Decision: "auto_low", PolicyIds: "p"),
            Ct);

        IReadOnlyList<AuditRecordView> journal = await world.Audit.QueryAsync(0, 200, Ct);
        return journal[^1].RecordId;
    }

    private static LifeEpisode Candidate(
        string evidenceRef, string kind = EpisodeKind.Milestone,
        string sensitivity = Sensitivity.Private) =>
        new("", Mind, kind, "2026-01-15T09:00:00.0000000+00:00", null,
            "Aurora ran its first capability",
            "It read the clock, which is as small a beginning as there is.",
            [evidenceRef], Significance.Medium, EpisodeStatus.Candidate, sensitivity, "");

    private static MemoryAccessContext Owner =>
        new("owner", [MemoryAccessPolicy.Owner], Sensitivity.Private);

    // ---- rule 1: evidence, and evidence that resolves ----

    [Fact]
    public async Task AnEpisodeWithNoEvidenceIsRefused()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        await Assert.ThrowsAsync<LifeHistoryException>(() =>
            world.History.ProposeAsync(Candidate("ref/1") with { EvidenceRefs = [] }, Ct));
    }

    [Fact]
    public async Task AnEpisodeWhoseEvidenceIsNotInTheJournalCannotBeVerified()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        LifeEpisode proposed = await world.History.ProposeAsync(Candidate("record/invented"), Ct);

        // An episode whose evidence does not resolve is a story. The difference between a story
        // and a history is exactly this check.
        LifeHistoryException refused = await Assert.ThrowsAsync<LifeHistoryException>(() =>
            world.History.VerifyAsync(proposed.Id, Ct));

        Assert.Contains("not in the audit journal", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProposingIsNotRememberingAndACandidateIsNeverNarrated()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        var evidence = await RecordAsync(world, "clock.now");

        await world.History.ProposeAsync(Candidate(evidence), Ct);

        CitedNarrative narrative = await world.History.NarrateAsync(Mind, Owner, Ct);

        Assert.Empty(narrative.Lines);
        Assert.Contains(narrative.Gaps, g => g.Contains("nothing has been verified", StringComparison.Ordinal));
    }

    // ---- rule 2: a record and a reading of one are different things ----

    [Fact]
    public async Task ARecordAndAReadingOfItAreDifferentLinesAndSayWhichIsWhich()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        var evidence = await RecordAsync(world, "clock.now");

        LifeEpisode proposed = await world.History.ProposeAsync(Candidate(evidence), Ct);
        await world.History.VerifyAsync(proposed.Id, Ct);

        CitedNarrative narrative = await world.History.NarrateAsync(Mind, Owner, Ct);

        NarrativeLine fact = narrative.Lines.Single(l => l.Confirmed);
        NarrativeLine reading = narrative.Lines.Single(l => !l.Confirmed);

        // A reader can tell which is which without being told, because they are different things
        // rather than different paragraphs.
        Assert.Equal(evidence, fact.EvidenceRef);
        Assert.Null(reading.EvidenceRef);
        Assert.Contains("small a beginning", reading.Text, StringComparison.Ordinal);
    }

    // ---- the limit case: not enough evidence is an answer ----

    [Fact]
    public async Task AskedAboutSomethingWithNoEvidenceAuroraSaysSoRatherThanChoosingAnEpisode()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        var evidence = await RecordAsync(world, "clock.now");

        LifeEpisode milestone = await world.History.ProposeAsync(Candidate(evidence), Ct);
        await world.History.VerifyAsync(milestone.Id, Ct);

        // There is a verified episode. It is not an incident, and the question was about incidents.
        CitedNarrative answer = await world.History.AnswerAsync(Mind, EpisodeKind.Incident, Owner, Ct);

        Assert.Empty(answer.Lines);
        Assert.Contains(
            answer.Gaps, g => g.Contains("not enough evidence", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AskedAboutSomethingWithEvidenceAuroraAnswersWithTheEarliestAndItsSource()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        var first = await RecordAsync(world, "files.write");
        var second = await RecordAsync(world, "files.write");

        LifeEpisode later = await world.History.ProposeAsync(
            Candidate(second, EpisodeKind.Incident) with
            {
                OccurredAtUtc = "2026-03-01T09:00:00.0000000+00:00", Title = "the second one",
            },
            Ct);

        LifeEpisode earlier = await world.History.ProposeAsync(
            Candidate(first, EpisodeKind.Incident) with
            {
                OccurredAtUtc = "2026-02-01T09:00:00.0000000+00:00", Title = "the first one",
            },
            Ct);

        await world.History.VerifyAsync(later.Id, Ct);
        await world.History.VerifyAsync(earlier.Id, Ct);

        CitedNarrative answer = await world.History.AnswerAsync(Mind, EpisodeKind.Incident, Owner, Ct);

        Assert.Equal("the first one", answer.Lines[0].Text);
        Assert.Equal(first, answer.Lines[0].EvidenceRef);
    }

    // ---- rule 3: the text is correctable, the journal is not touched ----

    [Fact]
    public async Task TheNarrativeCanBeCorrectedAndTheEvidenceStaysExactlyWhereItWas()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        var evidence = await RecordAsync(world, "clock.now");

        LifeEpisode proposed = await world.History.ProposeAsync(Candidate(evidence), Ct);
        await world.History.VerifyAsync(proposed.Id, Ct);

        LifeEpisode corrected = await world.History.CorrectAsync(
            proposed.Id, "It read the clock. Nothing more happened that day.",
            "paulo", "the original was grandiose", Ct);

        Assert.Contains("Nothing more happened", corrected.NarrativeSummary, StringComparison.Ordinal);

        // The evidence is untouched, and so is the journal — nothing in the correction path can
        // reach either, which is a stronger guarantee than remembering not to.
        Assert.Equal(proposed.EvidenceRefs, corrected.EvidenceRefs);
        Assert.True((await world.Audit.VerifyChainAsync(Ct)).Ok);

        EpisodeRevision revision = Assert.Single(await world.History.RevisionsAsync(proposed.Id, Ct));
        Assert.Equal("paulo", revision.Actor);
        Assert.Contains("grandiose", revision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACorrectionRecordsWhoAndWhy()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        var evidence = await RecordAsync(world, "clock.now");
        LifeEpisode proposed = await world.History.ProposeAsync(Candidate(evidence), Ct);

        await Assert.ThrowsAsync<LifeHistoryException>(() =>
            world.History.CorrectAsync(proposed.Id, "new text", "", "reason", Ct));

        await Assert.ThrowsAsync<LifeHistoryException>(() =>
            world.History.CorrectAsync(proposed.Id, "new text", "paulo", "", Ct));
    }

    // ---- limit case: a retracted episode leaves the narrative and stays on record ----

    [Fact]
    public async Task ARetractedEpisodeLeavesTheNarrativeAndKeepsItsTrail()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        var evidence = await RecordAsync(world, "clock.now");

        LifeEpisode proposed = await world.History.ProposeAsync(Candidate(evidence), Ct);
        await world.History.VerifyAsync(proposed.Id, Ct);
        await world.History.CorrectAsync(proposed.Id, "revised", "paulo", "clarity", Ct);

        Assert.NotEmpty((await world.History.NarrateAsync(Mind, Owner, Ct)).Lines);

        await world.History.RetractAsync(proposed.Id, "this was somebody else's milestone", "paulo", Ct);

        Assert.Empty((await world.History.NarrateAsync(Mind, Owner, Ct)).Lines);

        // The trail of having believed something about oneself is part of the history even when
        // the episode is not.
        LifeEpisode retracted = (await world.History.GetAsync(proposed.Id, Ct))!;
        Assert.Equal(EpisodeStatus.Retracted, retracted.Status);
        Assert.Contains("somebody else's", retracted.RetractedReason!, StringComparison.Ordinal);
        Assert.NotEmpty(await world.History.RevisionsAsync(proposed.Id, Ct));
    }

    [Fact]
    public async Task ARetractedEpisodeIsNotVerifiedBackIntoExistence()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        var evidence = await RecordAsync(world, "clock.now");

        LifeEpisode proposed = await world.History.ProposeAsync(Candidate(evidence), Ct);
        await world.History.RetractAsync(proposed.Id, "wrong", "paulo", Ct);

        await Assert.ThrowsAsync<LifeHistoryException>(() =>
            world.History.VerifyAsync(proposed.Id, Ct));
    }

    // ---- rule 4: sensitive material stays out, and the gap is declared ----

    [Fact]
    public async Task AnEpisodeAboveTheAudienceSCeilingIsWithheldAndTheGapIsDeclared()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        var ordinary = await RecordAsync(world, "clock.now");
        var delicate = await RecordAsync(world, "memory.remember");

        LifeEpisode open = await world.History.ProposeAsync(
            Candidate(ordinary, sensitivity: Sensitivity.Public), Ct);
        LifeEpisode closed = await world.History.ProposeAsync(
            Candidate(delicate, sensitivity: Sensitivity.Confidential) with { Title = "something private" },
            Ct);

        await world.History.VerifyAsync(open.Id, Ct);
        await world.History.VerifyAsync(closed.Id, Ct);

        var stranger = new MemoryAccessContext("stranger", ["policy/other"], Sensitivity.Public);
        CitedNarrative told = await world.History.NarrateAsync(Mind, stranger, Ct);

        Assert.DoesNotContain(told.Lines, l => l.Text.Contains("private", StringComparison.Ordinal));

        // Withheld rather than paraphrased: a redacted episode still discloses that something
        // happened, and the honest move is to say a gap exists without describing what is in it.
        Assert.Contains(told.Gaps, g => g.Contains("above what this audience", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheOwnerSeesWhatTheStrangerDoesNot()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        var delicate = await RecordAsync(world, "memory.remember");

        LifeEpisode closed = await world.History.ProposeAsync(
            Candidate(delicate, sensitivity: Sensitivity.Private) with { Title = "something private" },
            Ct);
        await world.History.VerifyAsync(closed.Id, Ct);

        CitedNarrative told = await world.History.NarrateAsync(Mind, Owner, Ct);

        Assert.Contains(told.Lines, l => l.Text.Contains("private", StringComparison.Ordinal));
        Assert.Empty(told.Gaps);
    }

    [Fact]
    public async Task TheNarrativeIsOrderedByWhenThingsHappened()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        var first = await RecordAsync(world, "clock.now");
        var second = await RecordAsync(world, "clock.now");

        LifeEpisode later = await world.History.ProposeAsync(
            Candidate(second) with
            {
                OccurredAtUtc = "2026-06-01T09:00:00.0000000+00:00", Title = "later",
                NarrativeSummary = "",
            },
            Ct);

        LifeEpisode earlier = await world.History.ProposeAsync(
            Candidate(first, EpisodeKind.Birth) with
            {
                OccurredAtUtc = "2026-01-01T09:00:00.0000000+00:00", Title = "earlier",
                NarrativeSummary = "",
            },
            Ct);

        await world.History.VerifyAsync(later.Id, Ct);
        await world.History.VerifyAsync(earlier.Id, Ct);

        CitedNarrative narrative = await world.History.NarrateAsync(Mind, Owner, Ct);

        Assert.Equal("earlier", narrative.Lines[0].Text);
        Assert.Equal("later", narrative.Lines[1].Text);
    }
}
