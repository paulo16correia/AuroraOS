using Aurora.Adapters.World;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>Conformance tests for RFC 041.</summary>
public sealed class WorldModelTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset T2020 = DateTimeOffset.Parse("2020-01-01T00:00:00+00:00");
    private static readonly DateTimeOffset T2024 = DateTimeOffset.Parse("2024-01-01T00:00:00+00:00");
    private static readonly DateTimeOffset T2026 = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");

    private static SqliteWorldModel New(SqliteTestDb db, DateTimeOffset? now = null) =>
        new(db.Factory, new TestClock(now ?? T2026), WorldModelOptions.Default);

    private static async Task<string> ActiveVersionAsync(SqliteWorldModel world)
    {
        WorldModelVersion version = await world.BeginVersionAsync("mind-1", null, Ct);
        await world.ActivateVersionAsync(version.Id, "owner", Ct);
        return version.Id;
    }

    private static WorldObservation Observed(
        string predicate = "works_in", string category = WorldPredicateCategory.Social,
        string subject = "person/paulo", string? objectRef = "project/aurora",
        DateTimeOffset? validFrom = null, DateTimeOffset? observedAt = null) =>
        new(subject, predicate, category, objectRef, null, ["email/1"], 0.9,
            Iso(observedAt ?? T2020), Iso(validFrom ?? T2020));

    private static string Iso(DateTimeOffset v) => v.ToString("O");

    // ---- rule 5: a tool observes, it does not conclude ----

    [Fact]
    public async Task AnObservationEntersAsProposed()
    {
        using var db = new SqliteTestDb();
        var world = New(db);
        var version = await ActiveVersionAsync(world);

        WorldAssertion assertion = await world.ObserveAsync(Observed(), version, Ct);

        Assert.Equal(WorldAssertionStatus.Proposed, assertion.Status);
    }

    [Fact]
    public async Task AToolCannotValidateItsOwnObservation()
    {
        using var db = new SqliteTestDb();
        var world = New(db);
        var version = await ActiveVersionAsync(world);
        WorldAssertion assertion = await world.ObserveAsync(Observed(), version, Ct);

        await Assert.ThrowsAsync<WorldModelException>(
            () => world.ValidateAsync(assertion.Id, SqliteWorldModel.ToolActor, [], Ct));
    }

    [Fact]
    public async Task AnObservationWithoutEvidenceIsRefused()
    {
        using var db = new SqliteTestDb();
        var world = New(db);
        var version = await ActiveVersionAsync(world);

        await Assert.ThrowsAsync<WorldModelException>(() => world.ObserveAsync(
            new WorldObservation("s", "p", WorldPredicateCategory.Social, "o", null, [], 1, Iso(T2020), Iso(T2020)),
            version, Ct));
    }

    // ---- rule 1: observed_at is not asserted_at, and "as of" works ----

    [Fact]
    public async Task ObservedAtAndAssertedAtAreSeparate()
    {
        using var db = new SqliteTestDb();
        var world = New(db, now: T2026);
        var version = await ActiveVersionAsync(world);

        WorldAssertion assertion = await world.ObserveAsync(Observed(observedAt: T2020), version, Ct);

        Assert.StartsWith("2020", assertion.ObservedAtUtc, StringComparison.Ordinal);
        Assert.StartsWith("2026", assertion.AssertedAtUtc, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsOfUsesHalfOpenIntervals()
    {
        using var db = new SqliteTestDb();
        var world = New(db);
        var version = await ActiveVersionAsync(world);

        WorldAssertion first = await world.ObserveAsync(Observed(validFrom: T2020), version, Ct);
        await world.ValidateAsync(first.Id, "owner", [], Ct);

        WorldAssertion second = await world.ObserveAsync(
            Observed(objectRef: "project/other", validFrom: T2024), version, Ct);
        await world.ValidateAsync(second.Id, "owner", [], Ct);

        // The boundary instant belongs to exactly one interval: [2020,2024) then [2024,∞).
        WorldAnswer before = await world.AskAsync("person/paulo", "works_in", T2024.AddSeconds(-1), Ct);
        WorldAnswer atBoundary = await world.AskAsync("person/paulo", "works_in", T2024, Ct);

        Assert.Equal("project/aurora", Assert.Single(before.Assertions).ObjectRef);
        Assert.Equal("project/other", Assert.Single(atBoundary.Assertions).ObjectRef);
    }

    [Fact]
    public async Task ReassociationEndsThePreviousWindowWithoutRewritingIt()
    {
        using var db = new SqliteTestDb();
        var world = New(db);
        var version = await ActiveVersionAsync(world);

        WorldAssertion first = await world.ObserveAsync(Observed(validFrom: T2020), version, Ct);
        await world.ValidateAsync(first.Id, "owner", [], Ct);
        WorldAssertion second = await world.ObserveAsync(
            Observed(objectRef: "project/other", validFrom: T2024), version, Ct);
        await world.ValidateAsync(second.Id, "owner", [], Ct);

        WorldAssertion closed = (await world.GetAsync(first.Id, Ct))!;

        Assert.Equal(WorldAssertionStatus.Historical, closed.Status);
        Assert.Equal("project/aurora", closed.ObjectRef);
        Assert.NotNull(closed.ValidToUtc);
    }

    // ---- rule 4: ignorance is representable ----

    [Fact]
    public async Task NothingRecordedIsReportedAsUnknown_NotAsFalse()
    {
        using var db = new SqliteTestDb();
        var world = New(db);
        await ActiveVersionAsync(world);

        WorldAnswer answer = await world.AskAsync("person/nobody", "works_in", T2026, Ct);

        Assert.Equal(WorldKnowledge.Unknown, answer.Knowledge);
        Assert.Contains("not evidence", answer.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnlyPastRecordsAreDistinguishedFromNoRecords()
    {
        using var db = new SqliteTestDb();
        var world = New(db);
        var version = await ActiveVersionAsync(world);
        WorldAssertion assertion = await world.ObserveAsync(
            Observed(validFrom: T2020), version, Ct);
        await world.ValidateAsync(assertion.Id, "owner", [], Ct);
        WorldAssertion next = await world.ObserveAsync(
            Observed(objectRef: "project/other", validFrom: T2024), version, Ct);
        await world.ValidateAsync(next.Id, "owner", [], Ct);

        WorldAnswer longAgo = await world.AskAsync("person/paulo", "works_in", T2020.AddYears(-5), Ct);

        Assert.Equal(WorldKnowledge.OnlyHistorical, longAgo.Knowledge);
    }

    // ---- rule 3: access is not ownership ----

    [Fact]
    public async Task OwningSomethingDoesNotImplyAccessToIt()
    {
        using var db = new SqliteTestDb();
        var world = New(db);
        var version = await ActiveVersionAsync(world);

        WorldAssertion owns = await world.ObserveAsync(
            Observed("has_account", WorldPredicateCategory.Ownership, objectRef: "discord/server-1"),
            version, Ct);
        await world.ValidateAsync(owns.Id, "owner", [], Ct);

        // "The person has Discord" does not mean Aurora can read that Discord.
        WorldAnswer access = await world.HasAccessAsync("person/paulo", "discord/server-1", T2026, Ct);

        Assert.Equal(WorldKnowledge.Unknown, access.Knowledge);
    }

    [Fact]
    public async Task EvidencedAccessIsReportedAsAsserted()
    {
        using var db = new SqliteTestDb();
        var world = New(db);
        var version = await ActiveVersionAsync(world);

        WorldAssertion granted = await world.ObserveAsync(
            Observed("can_read", WorldPredicateCategory.Access, objectRef: "discord/server-1"), version, Ct);
        await world.ValidateAsync(granted.Id, "owner", ["oauth/grant-1"], Ct);

        WorldAnswer access = await world.HasAccessAsync("person/paulo", "discord/server-1", T2026, Ct);

        Assert.Equal(WorldKnowledge.Asserted, access.Knowledge);
        Assert.Contains("oauth/grant-1", Assert.Single(access.Assertions).EvidenceRefs);
    }

    // ---- rule 2: identity resolution defers rather than guesses ----

    [Fact]
    public async Task AStrongMatchWithEvidenceMatches()
    {
        using var db = new SqliteTestDb();

        EntityResolution resolution = await New(db).ResolveAsync(
            new EntityCandidate("Paulo C.", "Person", ["email/1"], "person/paulo", 0.95), "owner", Ct);

        Assert.Equal(ResolutionDecision.Match, resolution.Decision);
        Assert.Equal("person/paulo", resolution.MatchedEntityRef);
    }

    [Fact]
    public async Task AWeakMatchDefersRatherThanGuessing()
    {
        using var db = new SqliteTestDb();

        EntityResolution resolution = await New(db).ResolveAsync(
            new EntityCandidate("Paulo C.", "Person", ["email/1"], "person/paulo", 0.6), "owner", Ct);

        Assert.Equal(ResolutionDecision.Defer, resolution.Decision);
        Assert.Null(resolution.MatchedEntityRef);
    }

    [Fact]
    public async Task NoCandidateWithEvidenceCreates()
    {
        using var db = new SqliteTestDb();

        EntityResolution resolution = await New(db).ResolveAsync(
            new EntityCandidate("Someone New", "Person", ["email/1"]), "owner", Ct);

        Assert.Equal(ResolutionDecision.Create, resolution.Decision);
    }

    // ---- limit cases ----

    [Fact]
    public async Task ContradictoryClaimsAboutTheSamePeriodStayDisputed()
    {
        using var db = new SqliteTestDb();
        var world = New(db);
        var version = await ActiveVersionAsync(world);

        WorldAssertion first = await world.ObserveAsync(
            Observed(objectRef: "vps/a", validFrom: T2020), version, Ct);
        await world.ValidateAsync(first.Id, "owner", [], Ct);

        WorldAssertion second = await world.ObserveAsync(
            Observed(objectRef: "vps/b", validFrom: T2020), version, Ct);
        WorldAssertion disputed = await world.ValidateAsync(second.Id, "owner", [], Ct);

        // No choice is inferred from which claim looks more plausible in text.
        Assert.Equal(WorldAssertionStatus.Disputed, disputed.Status);
        Assert.Equal(WorldAssertionStatus.Disputed, (await world.GetAsync(first.Id, Ct))!.Status);

        WorldAnswer answer = await world.AskAsync("person/paulo", "works_in", T2026, Ct);
        Assert.Equal(WorldKnowledge.Disputed, answer.Knowledge);
    }

    [Fact]
    public async Task ADeletedExternalEntityKeepsItsEvidence()
    {
        using var db = new SqliteTestDb();
        var world = New(db);
        var version = await ActiveVersionAsync(world);
        WorldAssertion assertion = await world.ObserveAsync(Observed(), version, Ct);
        await world.ValidateAsync(assertion.Id, "owner", [], Ct);

        var affected = await world.MarkInaccessibleAsync("person/paulo", "account closed", Ct);

        Assert.Equal(1, affected);

        WorldAssertion kept = (await world.GetAsync(assertion.Id, Ct))!;
        Assert.Equal(WorldAssertionStatus.Inaccessible, kept.Status);
        Assert.Equal(["email/1"], kept.EvidenceRefs);
    }

    [Fact]
    public async Task ADraftVersionIsNotASourceForDecisions()
    {
        using var db = new SqliteTestDb();
        var world = New(db);

        // A partial import: the version is still DRAFT.
        WorldModelVersion draft = await world.BeginVersionAsync("mind-1", null, Ct);
        WorldAssertion assertion = await world.ObserveAsync(Observed(), draft.Id, Ct);

        // Not even validatable, because nothing in a draft answers a question yet.
        WorldAnswer answer = await world.AskAsync("person/paulo", "works_in", T2026, Ct);
        Assert.Equal(WorldKnowledge.Unknown, answer.Knowledge);

        await world.ActivateVersionAsync(draft.Id, "owner", Ct);
        await world.ValidateAsync(assertion.Id, "owner", [], Ct);

        Assert.Equal(
            WorldKnowledge.Asserted,
            (await world.AskAsync("person/paulo", "works_in", T2026, Ct)).Knowledge);
    }

    [Fact]
    public async Task AToolCannotActivateAVersion()
    {
        using var db = new SqliteTestDb();
        var world = New(db);
        WorldModelVersion draft = await world.BeginVersionAsync("mind-1", null, Ct);

        await Assert.ThrowsAsync<WorldModelException>(
            () => world.ActivateVersionAsync(draft.Id, SqliteWorldModel.ToolActor, Ct));
    }
}
