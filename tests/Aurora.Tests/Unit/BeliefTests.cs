using System.Globalization;
using Aurora.Adapters.Beliefs;
using Aurora.Adapters.Persistence;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// The belief system (RFC 028): useful patterns that are never mistaken for facts.
/// </summary>
public sealed class BeliefTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static (SqliteBeliefSystem Beliefs, TestClock Clock) Build(
        SqliteTestDb db, string now = "2026-01-15T09:00:00+00:00")
    {
        var clock = new TestClock(At(now));
        return (new SqliteBeliefSystem(db.Factory, BeliefPolicy.Default, clock), clock);
    }

    private static BeliefCandidate ShortAnswers(double confidence = 0.7) =>
        new("person/owner", "prefers", """{"style":"short answers"}""",
            BeliefBasis.Observed, confidence);

    private static MemoryAccessContext Owner =>
        new("owner", [MemoryAccessPolicy.Owner], Sensitivity.Private);

    // ---- rule 1: the model alone is not evidence ----

    [Fact]
    public async Task ABeliefSupportedOnlyByTheModelSOwnOutputIsRefused()
    {
        using var db = new SqliteTestDb();
        var (beliefs, _) = Build(db);

        // A pattern the reasoner asserts about its own reasoning is not a second opinion; it is
        // the same one, restated.
        BeliefException refused = await Assert.ThrowsAsync<BeliefException>(() =>
            beliefs.ProposeAsync(ShortAnswers(), ["model/gpt-1", "thought/abc"], Ct));

        Assert.Contains("not evidence for itself", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABeliefWithNoSupportAtAllIsRefused()
    {
        using var db = new SqliteTestDb();
        var (beliefs, _) = Build(db);

        await Assert.ThrowsAsync<BeliefException>(() => beliefs.ProposeAsync(ShortAnswers(), [], Ct));
    }

    [Fact]
    public async Task OneRealObservationAlongsideModelOutputIsEnough()
    {
        using var db = new SqliteTestDb();
        var (beliefs, _) = Build(db);

        // The rule is that the model cannot be the *whole* case, not that it may not appear in one.
        Belief belief = await beliefs.ProposeAsync(
            ShortAnswers(), ["model/gpt-1", "conversation/12"], Ct);

        Assert.Equal(BeliefStatus.Active, belief.Status);
    }

    // ---- rule 2: never the sole basis for what matters ----

    [Theory]
    [InlineData(BeliefPurpose.Identity)]
    [InlineData(BeliefPurpose.Security)]
    [InlineData(BeliefPurpose.Money)]
    [InlineData(BeliefPurpose.Health)]
    [InlineData(BeliefPurpose.Law)]
    [InlineData(BeliefPurpose.SensitiveContent)]
    public async Task ABeliefIsNeverEnoughOnItsOwnForWhatMatters(string purpose)
    {
        using var db = new SqliteTestDb();
        var (beliefs, _) = Build(db);

        await beliefs.ProposeAsync(ShortAnswers(0.99), ["conversation/12"], Ct);

        BeliefSupport support = await beliefs.SupportAsync("person/owner", purpose, Owner, Ct);

        // The answer is the same however confident the belief is, and it travels with the beliefs
        // so a caller cannot get them without also getting the fact that they are not enough.
        Assert.False(support.MayBeSoleBasis);
        Assert.NotEmpty(support.Beliefs);
        Assert.Contains(purpose, support.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOrdinaryPurposeMayLeanOnAConfidentBelief()
    {
        using var db = new SqliteTestDb();
        var (beliefs, _) = Build(db);

        await beliefs.ProposeAsync(ShortAnswers(0.8), ["conversation/12"], Ct);

        BeliefSupport support = await beliefs.SupportAsync(
            "person/owner", BeliefPurpose.Ordinary, Owner, Ct);

        Assert.True(support.MayBeSoleBasis);
        Assert.Single(support.Beliefs);
    }

    // ---- insufficient data stays CANDIDATE and personalises nothing ----

    [Fact]
    public async Task AWeaklySupportedBeliefStaysACandidateAndIsNotOfferedAsSupport()
    {
        using var db = new SqliteTestDb();
        var (beliefs, _) = Build(db);

        Belief belief = await beliefs.ProposeAsync(ShortAnswers(0.3), ["conversation/12"], Ct);

        Assert.Equal(BeliefStatus.Candidate, belief.Status);

        BeliefSupport support = await beliefs.SupportAsync(
            "person/owner", BeliefPurpose.Ordinary, Owner, Ct);

        // Nothing material is personalised from a hunch.
        Assert.Empty(support.Beliefs);
        Assert.False(support.MayBeSoleBasis);
    }

    [Fact]
    public async Task ObservingEnoughTimesPromotesACandidate()
    {
        using var db = new SqliteTestDb();
        var (beliefs, _) = Build(db);

        Belief belief = await beliefs.ProposeAsync(ShortAnswers(0.3), ["conversation/12"], Ct);
        await beliefs.ObserveAsync(belief.Id, "conversation/15", 0.3, "asked for less again", Ct);

        Belief promoted = (await beliefs.GetAsync(belief.Id, Ct))!;

        Assert.Equal(BeliefStatus.Active, promoted.Status);
        Assert.Contains("conversation/15", promoted.EvidenceForRefs);
    }

    // ---- contradiction is answered by narrowing, never by averaging ----

    [Fact]
    public async Task AContradictionChallengesTheBeliefWithoutSplittingTheDifference()
    {
        using var db = new SqliteTestDb();
        var (beliefs, _) = Build(db);

        Belief belief = await beliefs.ProposeAsync(ShortAnswers(0.8), ["conversation/12"], Ct);

        Belief challenged = await beliefs.ChallengeAsync(
            belief.Id, "conversation/40", "asked for a long explanation about the tax code", Ct);

        Assert.Equal(BeliefStatus.Challenged, challenged.Status);
        Assert.Contains("conversation/40", challenged.EvidenceAgainstRefs);

        // Averaging turns two incompatible observations into one lukewarm claim that describes
        // neither, so the confidence is left exactly where it was.
        Assert.Equal(0.8, challenged.Confidence, 3);

        // And it stops being usable while it stands contradicted.
        BeliefSupport support = await beliefs.SupportAsync(
            "person/owner", BeliefPurpose.Ordinary, Owner, Ct);

        Assert.Empty(support.Beliefs);
    }

    [Fact]
    public async Task NarrowingTheScopeIsWhatAnswersAContradiction()
    {
        using var db = new SqliteTestDb();
        var (beliefs, _) = Build(db);

        Belief belief = await beliefs.ProposeAsync(ShortAnswers(0.8), ["conversation/12"], Ct);
        await beliefs.ChallengeAsync(belief.Id, "conversation/40", "wanted detail on tax", Ct);

        Belief narrowed = await beliefs.NarrowAsync(
            belief.Id, """{"except":["tax","legal"]}""", "holds outside tax and legal questions", Ct);

        Assert.Equal(BeliefStatus.Active, narrowed.Status);
        Assert.Contains("tax", narrowed.ScopeJson, StringComparison.Ordinal);

        // Reactivating without narrowing would be answering the contradiction by ignoring it.
        await beliefs.ChallengeAsync(belief.Id, "conversation/55", "again", Ct);

        await Assert.ThrowsAsync<BeliefException>(() =>
            beliefs.NarrowAsync(belief.Id, """{"except":["tax","legal"]}""", "unchanged", Ct));
    }

    // ---- a failed prediction is recorded, never erased ----

    [Fact]
    public async Task AFailedPredictionLeavesCounterEvidenceAndAHistory()
    {
        using var db = new SqliteTestDb();
        var (beliefs, _) = Build(db);

        Belief belief = await beliefs.ProposeAsync(ShortAnswers(0.8), ["conversation/12"], Ct);
        await beliefs.ObserveAsync(belief.Id, "conversation/40", -0.5, "wanted a long answer", Ct);

        Belief weakened = (await beliefs.GetAsync(belief.Id, Ct))!;

        Assert.Equal(0.3, weakened.Confidence, 3);
        Assert.Contains("conversation/40", weakened.EvidenceAgainstRefs);
        Assert.DoesNotContain("conversation/40", weakened.EvidenceForRefs);

        // The record of having believed something wrong is the useful part.
        IReadOnlyList<BeliefUpdate> history = await beliefs.UpdatesAsync(belief.Id, Ct);

        BeliefUpdate update = Assert.Single(history);
        Assert.Equal(-0.5, update.DeltaConfidence, 3);
        Assert.Contains("long answer", update.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARetractedBeliefKeepsItsHistoryRatherThanDisappearing()
    {
        using var db = new SqliteTestDb();
        var (beliefs, _) = Build(db);

        Belief belief = await beliefs.ProposeAsync(ShortAnswers(), ["conversation/12"], Ct);
        await beliefs.RetractAsync(belief.Id, "the owner said this was never true", Ct);

        Belief retracted = (await beliefs.GetAsync(belief.Id, Ct))!;

        Assert.Equal(BeliefStatus.Retracted, retracted.Status);
        Assert.NotEmpty(await beliefs.UpdatesAsync(belief.Id, Ct));
    }

    // ---- rule 3: beliefs weaken when nothing confirms them ----

    [Fact]
    public async Task ABeliefNobodyConfirmsWeakensAndEventuallyStopsBeingUsable()
    {
        using var db = new SqliteTestDb();
        var (beliefs, clock) = Build(db);

        Belief belief = await beliefs.ProposeAsync(ShortAnswers(0.8), ["conversation/12"], Ct);

        // One half-life later.
        clock.UtcNow = At("2026-01-22T09:00:00+00:00");
        await beliefs.ReviewDueAsync(Ct);

        Belief aged = (await beliefs.GetAsync(belief.Id, Ct))!;

        // A belief that never weakened would be a fact, which is the confusion this system exists
        // to prevent.
        Assert.Equal(0.4, aged.Confidence, 2);
        Assert.Equal(BeliefStatus.Candidate, aged.Status);
    }

    [Fact]
    public async Task ABeliefPastItsReviewDateExpires()
    {
        using var db = new SqliteTestDb();
        var (beliefs, clock) = Build(db);

        Belief belief = await beliefs.ProposeAsync(ShortAnswers(0.9), ["conversation/12"], Ct);

        clock.UtcNow = At("2026-03-01T09:00:00+00:00");
        await beliefs.ReviewDueAsync(Ct);

        Assert.Equal(BeliefStatus.Expired, (await beliefs.GetAsync(belief.Id, Ct))!.Status);
    }

    // ---- rule 4: what the person said outranks inference, and stays correctable ----

    [Fact]
    public async Task WhatThePersonSaidIsStillABeliefAndStillCorrectable()
    {
        using var db = new SqliteTestDb();
        var (beliefs, _) = Build(db);

        Belief stated = await beliefs.ProposeAsync(
            ShortAnswers(0.95) with { Basis = BeliefBasis.UserStated }, ["conversation/1"], Ct);

        Assert.Equal(BeliefBasis.UserStated, stated.Basis);

        // Rule 4 says a direct statement may prevail, not that it stops needing to be true.
        Belief challenged = await beliefs.ChallengeAsync(
            stated.Id, "conversation/60", "they have asked for detail repeatedly since", Ct);

        Assert.Equal(BeliefStatus.Challenged, challenged.Status);
    }
}
