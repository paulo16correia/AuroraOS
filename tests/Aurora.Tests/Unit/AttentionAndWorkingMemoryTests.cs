using Aurora.Adapters.Cognition;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>Conformance tests for RFC 023 and RFC 024.</summary>
public sealed class AttentionAndWorkingMemoryTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");
    private const string CycleId = "cycle-1";

    private static SqliteAttentionSystem Attention(SqliteTestDb db, DateTimeOffset? now = null) =>
        new(db.Factory, new SensitivityAttentionAuthorization(), new TestClock(now ?? Now));

    private static SqliteWorkingMemory Working(
        SqliteTestDb db, DateTimeOffset? now = null, TimeSpan? ttl = null) =>
        new(db.Factory, new TestClock(now ?? Now),
            new WorkingMemoryOptions(ttl ?? TimeSpan.FromMinutes(30)));

    private static AttentionItem Candidate(
        string reference, double relevance = 0.9, double urgency = 0.5,
        string sensitivity = Sensitivity.Private, int tokens = 100, string? expires = null) =>
        new(reference, AttentionKind.Memory, relevance, urgency, 0.5, 0.5, 0.9, 0.9,
            sensitivity, tokens, expires);

    private static MemoryAccessContext Access(string max = Sensitivity.Confidential) =>
        new("owner", ["policy/owner"], max);

    // ---- RFC 023 rule 1 and 4 ----

    [Fact]
    public async Task AnUnauthorisedItemIsExcludedBeforeItIsEverScored()
    {
        using var db = new SqliteTestDb();

        AttentionSet set = await Attention(db).RankAsync(
            CycleId, [Candidate("secret/1", sensitivity: Sensitivity.Secret)],
            AttentionPolicy.Default, Access(max: Sensitivity.Private), Ct);

        AttentionItem excluded = Assert.Single(set.Excluded);
        Assert.Contains(AttentionReason.NotAuthorised, excluded.ReasonCodes!);
        Assert.Equal(0, excluded.Score);
    }

    [Fact]
    public async Task UrgencyDoesNotBuyAccess()
    {
        using var db = new SqliteTestDb();

        // Maximum urgency on an item the caller may not see. RFC 023 rule 4 exists for exactly
        // this: a hostile instruction does not become readable by shouting.
        AttentionSet set = await Attention(db).RankAsync(
            CycleId,
            [Candidate("secret/1", relevance: 1, urgency: 1, sensitivity: Sensitivity.Secret)],
            AttentionPolicy.Default, Access(max: Sensitivity.Private), Ct);

        Assert.Empty(set.Items);
        Assert.Contains(AttentionReason.NotAuthorised, Assert.Single(set.Excluded).ReasonCodes!);
    }

    [Fact]
    public async Task AnItemAboveThePolicyCeilingIsExcludedEvenWhenTheCallerCouldSeeIt()
    {
        using var db = new SqliteTestDb();
        var policy = AttentionPolicy.Default with { SensitivityCeiling = Sensitivity.Private };

        AttentionSet set = await Attention(db).RankAsync(
            CycleId, [Candidate("c/1", sensitivity: Sensitivity.Confidential)],
            policy, Access(max: Sensitivity.Secret), Ct);

        Assert.Contains(AttentionReason.AboveSensitivityCeiling, Assert.Single(set.Excluded).ReasonCodes!);
    }

    // ---- RFC 023 rule 2: bounded by items and budget ----

    [Fact]
    public async Task TheItemLimitIsRespectedAndTheReasonRecorded()
    {
        using var db = new SqliteTestDb();
        var policy = AttentionPolicy.Default with { MaxItems = 2 };
        var candidates = Enumerable.Range(1, 5).Select(i => Candidate($"m/{i}")).ToList();

        AttentionSet set = await Attention(db).RankAsync(CycleId, candidates, policy, Access(), Ct);

        Assert.Equal(2, set.Items.Count);
        Assert.All(set.Excluded, e => Assert.Contains(AttentionReason.ItemLimitReached, e.ReasonCodes!));
    }

    [Fact]
    public async Task TheTokenBudgetIsRespected()
    {
        using var db = new SqliteTestDb();
        var policy = AttentionPolicy.Default with { TokenBudget = 250, MaxItems = 10 };
        var candidates = Enumerable.Range(1, 5).Select(i => Candidate($"m/{i}", tokens: 100)).ToList();

        AttentionSet set = await Attention(db).RankAsync(CycleId, candidates, policy, Access(), Ct);

        Assert.Equal(2, set.Items.Count);
        Assert.Contains(set.Excluded, e => e.ReasonCodes!.Contains(AttentionReason.BudgetExhausted));
    }

    // ---- RFC 023 rule 3: reasons are recorded for both sides ----

    [Fact]
    public async Task EverySelectionAndExclusionCarriesItsReason()
    {
        using var db = new SqliteTestDb();
        var policy = AttentionPolicy.Default with { MaxItems = 1 };

        AttentionSet set = await Attention(db).RankAsync(
            CycleId, [Candidate("m/1"), Candidate("m/2", relevance: 0.01, urgency: 0.01)],
            policy, Access(), Ct);

        Assert.All(set.Items, i => Assert.NotEmpty(i.ReasonCodes!));
        Assert.All(set.Excluded, i => Assert.NotEmpty(i.ReasonCodes!));

        // And they survive a round trip, because the record is the point.
        AttentionSet reloaded = (await Attention(db).GetAsync(CycleId, Ct))!;
        Assert.All(reloaded.Excluded, i => Assert.NotEmpty(i.ReasonCodes!));
    }

    [Fact]
    public async Task AnExpiredCandidateIsExcluded()
    {
        using var db = new SqliteTestDb();

        AttentionSet set = await Attention(db).RankAsync(
            CycleId, [Candidate("m/1", expires: Now.AddMinutes(-1).ToString("O"))],
            AttentionPolicy.Default, Access(), Ct);

        Assert.Contains(AttentionReason.Expired, Assert.Single(set.Excluded).ReasonCodes!);
    }

    [Fact]
    public async Task NoCandidatesProducesAnEmptySetRatherThanAFailure()
    {
        using var db = new SqliteTestDb();

        AttentionSet set = await Attention(db).RankAsync(CycleId, [], AttentionPolicy.Default, Access(), Ct);

        Assert.Empty(set.Items);
        Assert.Equal(AttentionSetStatus.Proposed, set.Status);
    }

    [Fact]
    public async Task RankingIsDeterministic()
    {
        using var db = new SqliteTestDb();
        var candidates = new[] { Candidate("m/b"), Candidate("m/a"), Candidate("m/c") };

        AttentionSet first = await Attention(db).RankAsync(CycleId, candidates, AttentionPolicy.Default, Access(), Ct);
        AttentionSet second = await Attention(db).RankAsync("cycle-2", candidates, AttentionPolicy.Default, Access(), Ct);

        Assert.Equal(first.Items.Select(i => i.Ref), second.Items.Select(i => i.Ref));
    }

    // ---- RFC 024: the frame ----

    [Fact]
    public async Task AFrameIsSeededFromWhatAttentionSelected()
    {
        using var db = new SqliteTestDb();
        AttentionSet set = await Attention(db).RankAsync(
            CycleId, [Candidate("m/1"), Candidate("m/2")], AttentionPolicy.Default, Access(), Ct);

        WorkingMemoryFrame frame = await Working(db).OpenAsync(CycleId, "session-1", set, AttentionPolicy.Default, Ct);

        Assert.Equal(2, frame.UsedItems);
        Assert.Equal(CycleId, frame.CycleId);
    }

    [Fact]
    public async Task AFullFrameRefusesLoudlyRatherThanTruncatingSilently()
    {
        using var db = new SqliteTestDb();
        var policy = AttentionPolicy.Default with { MaxItems = 1, TokenBudget = 100 };
        AttentionSet set = await Attention(db).RankAsync(
            CycleId, [Candidate("m/1", tokens: 100)], policy, Access(), Ct);
        var working = Working(db);
        WorkingMemoryFrame frame = await working.OpenAsync(CycleId, null, set, policy, Ct);

        await Assert.ThrowsAsync<WorkingMemoryFullException>(() => working.PutAsync(
            frame.Id, Draft("d/1", tokens: 100), Ct));
    }

    [Fact]
    public async Task AnItemAboveTheFrameCeilingIsRefused()
    {
        using var db = new SqliteTestDb();
        var policy = AttentionPolicy.Default with { SensitivityCeiling = Sensitivity.Private };
        var working = Working(db);
        WorkingMemoryFrame frame = await working.OpenAsync(
            CycleId, null, await Attention(db).RankAsync(CycleId, [], policy, Access(), Ct), policy, Ct);

        await Assert.ThrowsAsync<WorkingMemoryException>(() => working.PutAsync(
            frame.Id, Draft("d/1") with { SensitivityClass = Sensitivity.Secret }, Ct));
    }

    [Fact]
    public async Task ContextExpires()
    {
        using var db = new SqliteTestDb();
        var policy = AttentionPolicy.Default;
        AttentionSet set = await Attention(db).RankAsync(CycleId, [], policy, Access(), Ct);
        WorkingMemoryFrame frame = await Working(db, ttl: TimeSpan.FromMinutes(30))
            .OpenAsync(CycleId, null, set, policy, Ct);

        var later = Working(db, now: Now.AddHours(1));
        var expired = await later.ExpireDueAsync(Ct);

        Assert.Equal(1, expired);
        Assert.Equal(WorkingMemoryStatus.Expired, (await later.GetAsync(frame.Id, Ct))!.Status);
    }

    [Fact]
    public async Task ASealedFrameTakesNothingMore()
    {
        using var db = new SqliteTestDb();
        var working = Working(db);
        WorkingMemoryFrame frame = await working.OpenAsync(
            CycleId, null, await Attention(db).RankAsync(CycleId, [], AttentionPolicy.Default, Access(), Ct),
            AttentionPolicy.Default, Ct);
        await working.SealAsync(frame.Id, Ct);

        await Assert.ThrowsAsync<WorkingMemoryException>(() => working.PutAsync(frame.Id, Draft("d/1"), Ct));
    }

    // ---- RFC 024 rule 3: a hypothesis never becomes a fact by itself ----

    [Fact]
    public async Task AHypothesisCanOnlyBeConsolidatedAsACandidate()
    {
        using var db = new SqliteTestDb();
        var working = Working(db);
        WorkingMemoryFrame frame = await working.OpenAsync(
            CycleId, null, await Attention(db).RankAsync(CycleId, [], AttentionPolicy.Default, Access(), Ct),
            AttentionPolicy.Default, Ct);

        WorkingItem guess = await working.PutAsync(
            frame.Id, Draft("h/1") with { Type = WorkingItemType.Hypothesis }, Ct);
        WorkingItem result = await working.PutAsync(
            frame.Id, Draft("r/1") with { Type = WorkingItemType.Result }, Ct);

        DisposalReport report = await working.DisposeFrameAsync(frame.Id, [
            new ConsolidationDecision(guess.Id, WorkingItemDisposition.Consolidate),
            new ConsolidationDecision(result.Id, WorkingItemDisposition.Consolidate),
        ], Ct);

        Assert.True(report.Consolidations.Single(c => c.WorkingItemId == guess.Id).MustEnterAsCandidate);
        Assert.False(report.Consolidations.Single(c => c.WorkingItemId == result.Id).MustEnterAsCandidate);
    }

    [Fact]
    public async Task DisposalDefaultsToDiscardingAndSummarisesWithoutExposingDrafts()
    {
        using var db = new SqliteTestDb();
        var working = Working(db);
        WorkingMemoryFrame frame = await working.OpenAsync(
            CycleId, null, await Attention(db).RankAsync(CycleId, [], AttentionPolicy.Default, Access(), Ct),
            AttentionPolicy.Default, Ct);
        await working.PutAsync(frame.Id, Draft("d/1") with { PayloadJson = """{"secret_reasoning":"x"}""" }, Ct);

        DisposalReport report = await working.DisposeFrameAsync(frame.Id, [], Ct);

        Assert.Equal(1, report.Discarded);
        Assert.DoesNotContain("secret_reasoning", report.Summary, StringComparison.Ordinal);
    }

    // ---- RFC 024 rule 1: sharing is explicit ----

    [Fact]
    public async Task ItemsMoveBetweenFramesOnlyByExplicitTransfer()
    {
        using var db = new SqliteTestDb();
        var working = Working(db);
        AttentionSet empty = await Attention(db).RankAsync(CycleId, [], AttentionPolicy.Default, Access(), Ct);
        WorkingMemoryFrame first = await working.OpenAsync(CycleId, null, empty, AttentionPolicy.Default, Ct);
        WorkingMemoryFrame second = await working.OpenAsync("cycle-2", null, empty, AttentionPolicy.Default, Ct);

        WorkingItem item = await working.PutAsync(first.Id, Draft("d/1"), Ct);

        Assert.Empty(await working.ItemsAsync(second.Id, Ct));

        await working.TransferAsync(item.Id, second.Id, "follow-up cycle needs it", Ct);

        Assert.Single(await working.ItemsAsync(second.Id, Ct));
        Assert.Empty(await working.ItemsAsync(first.Id, Ct));
    }

    private static WorkingItem Draft(string reference, int tokens = 10) => new(
        Guid.NewGuid().ToString("N"), string.Empty, WorkingItemType.Draft,
        PayloadJson: "{}", PayloadRef: null, SourceRefs: [reference], Confidence: 0.5,
        SensitivityClass: Sensitivity.Private, TokenCost: tokens,
        CreatedAtUtc: Now.ToString("O"), ExpiresAtUtc: null,
        Disposition: WorkingItemDisposition.Pending);
}
