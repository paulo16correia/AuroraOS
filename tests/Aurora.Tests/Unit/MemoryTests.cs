using Aurora.Adapters.Memories;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>Conformance tests for RFC 03.</summary>
public sealed class MemoryTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed class BrokenRanker : IMemoryRanker
    {
        public IReadOnlyList<RankedMemory> Rank(string query, IReadOnlyList<MemoryRecord> permitted) =>
            throw new InvalidOperationException("index unavailable");
    }

    private static SqliteMemoryService Service(SqliteTestDb db, IMemoryRanker? ranker = null) =>
        new(db.Factory, ranker ?? new LexicalMemoryRanker(), new TestClock(DateTimeOffset.UnixEpoch));

    private static MemoryCandidate Candidate(
        string predicate = "prefers", string objectJson = """{"drink":"tea"}""",
        string summary = "Paulo prefers tea", string sensitivity = Sensitivity.Private,
        string kind = MemoryKind.Semantic) =>
        new(kind, "person/paulo", predicate, objectJson, summary, 0.8, sensitivity);

    private static MemoryProvenance From(
        string createdBy = MemoryOrigin.User, string? rule = null, params string[] sources) =>
        new(sources.Length == 0 ? ["conversation/1"] : sources, ["message/7"], createdBy, "policy/owner", rule);

    private static MemoryAccessContext Owner(string max = Sensitivity.Secret) =>
        new("owner", ["policy/owner"], max);

    // ---- rule 1: origin and access policy, or it is not persisted ----

    [Fact]
    public async Task AMemoryWithoutAnOriginIsRefused()
    {
        using var db = new SqliteTestDb();

        await Assert.ThrowsAsync<MemoryException>(() => Service(db).RecordAsync(
            Candidate(), new MemoryProvenance([], ["e"], MemoryOrigin.User, "policy/owner"), Ct));
    }

    [Fact]
    public async Task AMemoryWithoutAnAccessPolicyIsRefused()
    {
        using var db = new SqliteTestDb();

        await Assert.ThrowsAsync<MemoryException>(() => Service(db).RecordAsync(
            Candidate(), new MemoryProvenance(["s"], ["e"], MemoryOrigin.User, ""), Ct));
    }

    [Fact]
    public async Task ProvenanceIsKeptOnTheRecord()
    {
        using var db = new SqliteTestDb();

        MemoryRecord memory = await Service(db).RecordAsync(Candidate(), From(), Ct);

        Assert.Equal(["conversation/1"], memory.SourceRefs);
        Assert.Equal(["message/7"], memory.EvidenceRefs);
        Assert.Equal(MemoryOrigin.User, memory.CreatedBy);
    }

    // ---- inferred facts start as candidates ----

    [Fact]
    public async Task AnInferredFactStartsAsACandidate()
    {
        using var db = new SqliteTestDb();

        MemoryRecord inferred = await Service(db).RecordAsync(Candidate(), From(MemoryOrigin.System), Ct);
        MemoryRecord stated = await Service(db).RecordAsync(
            Candidate(predicate: "lives_in"), From(MemoryOrigin.User), Ct);

        Assert.Equal(MemoryStatus.Candidate, inferred.Status);
        Assert.Equal(MemoryStatus.Active, stated.Status);
    }

    [Fact]
    public async Task CandidatesCanBeExcludedFromResults()
    {
        using var db = new SqliteTestDb();
        var service = Service(db);
        await service.RecordAsync(Candidate(), From(MemoryOrigin.System), Ct);

        MemorySearchResult without = await service.SearchAsync(
            "tea", Owner(), new MemoryFilters(IncludeCandidates: false), Ct);

        Assert.Empty(without.Matches);
    }

    // ---- rule 5: sensitive material needs a specific rule ----

    [Theory]
    [InlineData(Sensitivity.Confidential)]
    [InlineData(Sensitivity.Secret)]
    public async Task SensitiveMaterialIsNotConsolidatedWithoutASpecificRule(string sensitivity)
    {
        using var db = new SqliteTestDb();

        await Assert.ThrowsAsync<MemoryException>(() => Service(db).RecordAsync(
            Candidate(sensitivity: sensitivity), From(), Ct));
    }

    [Fact]
    public async Task SensitiveMaterialIsAllowedWithTheRule()
    {
        using var db = new SqliteTestDb();

        MemoryRecord memory = await Service(db).RecordAsync(
            Candidate(sensitivity: Sensitivity.Confidential), From(rule: "rule/medical-consent"), Ct);

        Assert.Equal(Sensitivity.Confidential, memory.SensitivityClass);
    }

    // ---- rule 2: access and classification before ranking ----

    [Fact]
    public async Task AMemoryOutsideTheCallersPolicyIsNeverReturned()
    {
        using var db = new SqliteTestDb();
        var service = Service(db);
        await service.RecordAsync(Candidate(), From(), Ct);

        MemorySearchResult result = await service.SearchAsync(
            "tea", new MemoryAccessContext("stranger", ["policy/other"], Sensitivity.Secret),
            new MemoryFilters(), Ct);

        Assert.Empty(result.Matches);
    }

    [Fact]
    public async Task AMemoryAboveTheCallersCeilingIsNeverReturned()
    {
        using var db = new SqliteTestDb();
        var service = Service(db);
        await service.RecordAsync(
            Candidate(sensitivity: Sensitivity.Confidential), From(rule: "rule/ok"), Ct);

        MemorySearchResult result = await service.SearchAsync(
            "tea", Owner(max: Sensitivity.Private), new MemoryFilters(), Ct);

        Assert.Empty(result.Matches);
    }

    [Fact]
    public async Task WorkingMemoryNeverEntersLastingResearch()
    {
        using var db = new SqliteTestDb();
        var service = Service(db);
        await service.RecordAsync(Candidate(kind: MemoryKind.Working), From(), Ct);

        MemorySearchResult result = await service.SearchAsync("tea", Owner(), new MemoryFilters(), Ct);

        Assert.Empty(result.Matches);
    }

    [Fact]
    public async Task SearchRanksWhatTheCallerMaySee()
    {
        using var db = new SqliteTestDb();
        var service = Service(db);
        await service.RecordAsync(Candidate(summary: "Paulo prefers tea"), From(), Ct);
        await service.RecordAsync(
            Candidate(predicate: "lives_in", objectJson: """{"city":"Porto"}""",
                summary: "Paulo lives in Porto"), From(), Ct);

        MemorySearchResult result = await service.SearchAsync("tea", Owner(), new MemoryFilters(), Ct);

        Assert.Single(result.Matches);
        Assert.True(result.Confident);
        Assert.Contains("tea", result.Matches[0].Memory.Summary, StringComparison.Ordinal);
    }

    // ---- limit case: index failure must not assert absence ----

    [Fact]
    public async Task ADegradedSearchSaysSo_RatherThanClaimingNothingExists()
    {
        using var db = new SqliteTestDb();
        await Service(db).RecordAsync(Candidate(), From(), Ct);

        MemorySearchResult result = await Service(db, new BrokenRanker())
            .SearchAsync("tea", Owner(), new MemoryFilters(), Ct);

        Assert.False(result.Confident);
        Assert.NotNull(result.Degradation);
        Assert.NotEmpty(result.Matches);
    }

    // ---- limit case: contradictions ----

    [Fact]
    public async Task ContradictoryMemoriesAreBothKeptAndBothDisputed()
    {
        using var db = new SqliteTestDb();
        var service = Service(db);
        MemoryRecord first = await service.RecordAsync(
            Candidate(objectJson: """{"drink":"tea"}"""), From(), Ct);
        MemoryRecord second = await service.RecordAsync(
            Candidate(objectJson: """{"drink":"coffee"}"""), From(), Ct);

        // Silently superseding one would destroy the evidence that they ever disagreed.
        Assert.Equal(MemoryStatus.Disputed, (await service.GetAsync(first.Id, Ct))!.Status);
        Assert.Equal(MemoryStatus.Disputed, (await service.GetAsync(second.Id, Ct))!.Status);
    }

    // ---- rule 3: the owner's correction prevails ----

    [Fact]
    public async Task AutomaticInferenceCannotOverrideAnOwnerCorrection()
    {
        using var db = new SqliteTestDb();
        var service = Service(db);
        MemoryRecord memory = await service.RecordAsync(Candidate(), From(), Ct);
        await service.ReviseAsync(memory.Id, RevisionOperation.Correct, MemoryOrigin.User, "it is coffee", Ct);

        await Assert.ThrowsAsync<MemoryException>(() => service.ReviseAsync(
            memory.Id, RevisionOperation.Correct, MemoryOrigin.System, "inferred otherwise", Ct));
    }

    [Fact]
    public async Task RevisionsFormAnAuditedChain()
    {
        using var db = new SqliteTestDb();
        var service = Service(db);
        MemoryRecord memory = await service.RecordAsync(Candidate(), From(), Ct);
        await service.ReviseAsync(memory.Id, RevisionOperation.Confirm, MemoryOrigin.User, "confirmed", Ct);

        IReadOnlyList<MemoryRevision> history = await service.RevisionsAsync(memory.Id, Ct);

        Assert.Equal(2, history.Count);
        Assert.Equal(RevisionOperation.Create, history[0].Operation);
        Assert.Null(history[0].PriorHash);
        Assert.Equal(history[0].NewHash, history[1].PriorHash);
    }

    // ---- rule 4: retraction leaves the trail, removes the reach ----

    [Fact]
    public async Task ForgettingRemovesItFromReasoningAndKeepsTheHistory()
    {
        using var db = new SqliteTestDb();
        var service = Service(db);
        MemoryRecord memory = await service.RecordAsync(Candidate(), From(), Ct);

        MemoryTombstone tombstone = await service.ForgetAsync(memory.Id, MemoryOrigin.User, Ct);

        Assert.True(tombstone.RemovedFromActiveIndexes);
        Assert.Equal(MemoryStatus.Retracted, (await service.GetAsync(memory.Id, Ct))!.Status);
        Assert.Empty((await service.SearchAsync("tea", Owner(), new MemoryFilters(), Ct)).Matches);

        // The revision history survives: how the system reached a conclusion is not erased.
        Assert.Equal(2, (await service.RevisionsAsync(memory.Id, Ct)).Count);
    }

    [Fact]
    public async Task TheTombstoneStatesTheRealScope()
    {
        using var db = new SqliteTestDb();
        var service = Service(db);
        MemoryRecord memory = await service.RecordAsync(Candidate(), From(), Ct);

        MemoryTombstone tombstone = await service.ForgetAsync(memory.Id, MemoryOrigin.User, Ct);

        Assert.Contains("audit", tombstone.Scope, StringComparison.OrdinalIgnoreCase);
    }
}
