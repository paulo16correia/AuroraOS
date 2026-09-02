using Aurora.Adapters.Presence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Voice sessions as they are actually stored, and the things that must hold across processes
/// (docs/adr/0073).
/// </summary>
/// <remarks>
/// The pure decision is tested in <see cref="VoiceAuthorizationTests"/>. What is tested here is
/// what only a store can get wrong: a provider's duplicate event opening a second session, two tool
/// requests both spending the last unit of a budget, and an operator's stop that leaves a call
/// running.
/// </remarks>
public sealed class VoiceSessionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-02T10:00:00Z");

    private static VoiceSession Session(
        string id = "vs-1",
        string? external = "CA-provider-1",
        int maxCalls = 3,
        VoiceChannel channel = VoiceChannel.Phone) =>
        new(id, channel, "fake", VoiceCallDirection.Inbound,
            new VoiceParticipant("+351911111111"),
            new VoiceGrant(["memory.recall"], maxCalls, TimeSpan.FromMinutes(10),
                Now.AddMinutes(30).ToString("O")),
            VoiceSessionState.Pending, Now.ToString("O"), "corr-1", external);

    private static SqliteVoiceSessionStore Store(SqliteTestDb db) =>
        new(db.Factory, new TestClock(Now));

    [Fact]
    public async Task ASessionSurvivesBeingWrittenAndReadBack()
    {
        using var db = new SqliteTestDb();
        SqliteVoiceSessionStore store = Store(db);

        await store.OpenAsync(Session(), CancellationToken.None);
        VoiceSession? found = await store.FindAsync("vs-1", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(VoiceChannel.Phone, found!.Channel);
        Assert.Equal("+351911111111", found.Participant.Handle);
        Assert.Equal(["memory.recall"], found.Grant.AllowedActions);
        Assert.Equal(TimeSpan.FromMinutes(10), found.Grant.MaxDuration);
    }

    [Fact]
    public async Task AnOutboundIntentIsStoredWithItsSession()
    {
        using var db = new SqliteTestDb();
        SqliteVoiceSessionStore store = Store(db);

        var intent = new OutboundCallIntent(
            "Remind about tomorrow's meeting", "Confirm they know the time",
            new VoiceParticipant("+351911111111"),
            new VoiceGrant([], 0, TimeSpan.FromMinutes(5), Now.AddMinutes(10).ToString("O")),
            ["do not discuss anything else"], "operator", "ap-1");

        await store.OpenAsync(
            Session() with { Direction = VoiceCallDirection.Outbound, Intent = intent },
            CancellationToken.None);

        VoiceSession found = (await store.FindAsync("vs-1", CancellationToken.None))!;

        // The reason a call was made has to survive the call. Afterwards, "why did Aurora ring
        // this person" is answerable from the record rather than from whoever remembers.
        Assert.Equal("Remind about tomorrow's meeting", found.Intent!.Purpose);
        Assert.Equal("ap-1", found.Intent.ApprovalRef);
    }

    // ---- providers repeat themselves ----

    [Fact]
    public async Task TheSameProviderEventTwiceResolvesToOneSession()
    {
        using var db = new SqliteTestDb();
        SqliteVoiceSessionStore store = Store(db);

        await store.OpenAsync(Session(), CancellationToken.None);

        VoiceSession? first = await store.FindByExternalAsync(
            "fake", "CA-provider-1", CancellationToken.None);
        VoiceSession? second = await store.FindByExternalAsync(
            "fake", "CA-provider-1", CancellationToken.None);

        // A webhook delivered twice is the ordinary case, not the exception. Both deliveries have
        // to land on the session the first one created.
        Assert.Equal(first!.SessionId, second!.SessionId);
    }

    [Fact]
    public async Task TwoSessionsCannotShareOneProviderCall()
    {
        using var db = new SqliteTestDb();
        SqliteVoiceSessionStore store = Store(db);

        await store.OpenAsync(Session("vs-1"), CancellationToken.None);

        // Enforced by the database rather than by whoever remembered to check first. A duplicate
        // event racing itself would otherwise open two sessions for one call, each with its own
        // budget.
        await Assert.ThrowsAnyAsync<Exception>(
            () => store.OpenAsync(Session("vs-2"), CancellationToken.None));
    }

    [Fact]
    public async Task TwoSessionsWithoutProviderIdentifiersDoNotCollide()
    {
        using var db = new SqliteTestDb();
        SqliteVoiceSessionStore store = Store(db);

        // A session that has not connected yet has no provider reference. The uniqueness rule is
        // partial for that reason — several of them can be pending at once.
        await store.OpenAsync(Session("vs-1", external: null), CancellationToken.None);
        await store.OpenAsync(Session("vs-2", external: null), CancellationToken.None);

        Assert.Equal(2, (await store.LiveAsync(CancellationToken.None)).Count);
    }

    // ---- budgets ----

    [Fact]
    public async Task ASessionSpendsItsBudgetAndThenHasNone()
    {
        using var db = new SqliteTestDb();
        SqliteVoiceSessionStore store = Store(db);

        await store.OpenAsync(Session(maxCalls: 2), CancellationToken.None);

        Assert.True((await store.SpendToolCallAsync("vs-1", CancellationToken.None)).Spent);
        Assert.True((await store.SpendToolCallAsync("vs-1", CancellationToken.None)).Spent);

        VoiceBudgetUse third = await store.SpendToolCallAsync("vs-1", CancellationToken.None);

        Assert.False(third.Spent);
        Assert.Equal(2, third.Limit);
    }

    [Fact]
    public async Task ConcurrentRequestsNeverOverspendABudget()
    {
        using var db = new SqliteTestDb();
        SqliteVoiceSessionStore store = Store(db);

        await store.OpenAsync(Session(maxCalls: 5), CancellationToken.None);

        VoiceBudgetUse[] results = await Task.WhenAll(
            Enumerable.Range(0, 20)
                .Select(_ => store.SpendToolCallAsync("vs-1", CancellationToken.None)));

        // Twenty requests, five units. The check and the spend are one statement, so exactly five
        // succeed however they interleave — the same rule consent sessions use.
        Assert.Equal(5, results.Count(r => r.Spent));

        VoiceSession after = (await store.FindAsync("vs-1", CancellationToken.None))!;
        Assert.Equal(5, after.ToolCallsUsed);
    }

    [Fact]
    public async Task SpendingAgainstAMissingSessionIsRefusedRatherThanThrown()
    {
        using var db = new SqliteTestDb();

        VoiceBudgetUse use = await Store(db)
            .SpendToolCallAsync("never-existed", CancellationToken.None);

        Assert.False(use.Spent);
    }

    // ---- lifecycle ----

    [Fact]
    public async Task EndingASessionRecordsWhenAndWhy()
    {
        using var db = new SqliteTestDb();
        SqliteVoiceSessionStore store = Store(db);

        await store.OpenAsync(Session(), CancellationToken.None);
        await store.AdvanceAsync("vs-1", VoiceSessionState.Active, null, CancellationToken.None);

        VoiceSession ended = await store.AdvanceAsync(
            "vs-1", VoiceSessionState.Ended, "the caller hung up", CancellationToken.None);

        Assert.Equal(VoiceSessionState.Ended, ended.State);
        Assert.Equal("the caller hung up", ended.EndedReason);
        Assert.NotNull(ended.EndedAtUtc);
        Assert.False(ended.IsLive);
    }

    [Fact]
    public async Task AdvancingToALiveStateDoesNotStampAnEndTime()
    {
        using var db = new SqliteTestDb();
        SqliteVoiceSessionStore store = Store(db);

        await store.OpenAsync(Session(), CancellationToken.None);
        VoiceSession active = await store.AdvanceAsync(
            "vs-1", VoiceSessionState.Active, null, CancellationToken.None);

        Assert.Null(active.EndedAtUtc);
        Assert.True(active.IsLive);
    }

    // ---- the operator's stop ----

    [Fact]
    public async Task StoppingVoiceEndsEveryLiveSessionOnEveryChannel()
    {
        using var db = new SqliteTestDb();
        SqliteVoiceSessionStore store = Store(db);
        var audit = new RecordingAuditStore();

        await store.OpenAsync(Session("vs-1", "CA-1"), CancellationToken.None);
        await store.OpenAsync(
            Session("vs-2", "DC-1", channel: VoiceChannel.Discord), CancellationToken.None);
        await store.AdvanceAsync("vs-2", VoiceSessionState.Active, null, CancellationToken.None);

        var policy = new VoicePolicyService(VoiceSettings.Default, store, audit);

        await policy.StopAsync("operator", "wrong call", CancellationToken.None);

        // Both channels. A stop that reached the telephone and left the Discord call talking would
        // be the wrong half of the job, which is why there is one table rather than two.
        Assert.Empty(await store.LiveAsync(CancellationToken.None));

        VoiceSession phone = (await store.FindAsync("vs-1", CancellationToken.None))!;
        Assert.Equal(VoiceSessionState.Cancelled, phone.State);
        Assert.Contains("wrong call", phone.EndedReason);
    }

    [Fact]
    public async Task StoppingVoiceIsRecordedInTheOrdinaryAudit()
    {
        using var db = new SqliteTestDb();
        var audit = new RecordingAuditStore();
        var policy = new VoicePolicyService(VoiceSettings.Default, Store(db), audit);

        await policy.StopAsync("operator", "wrong call", CancellationToken.None);
        await policy.ResumeAsync("operator", CancellationToken.None);

        // The same chain as every other decision about what Aurora may do, not a log of its own.
        Assert.Contains(audit.Entries, e => e.ActionId == "voice.stopped");
        Assert.Contains(audit.Entries, e => e.ActionId == "voice.resumed");
        Assert.Contains(audit.Entries, e => e.Reason!.Contains("wrong call"));
    }

    [Fact]
    public async Task StoppedIsReportedImmediatelyRatherThanAtTheNextRestart()
    {
        using var db = new SqliteTestDb();
        var policy = new VoicePolicyService(
            VoiceSettings.Default, Store(db), new RecordingAuditStore());

        Assert.False((await policy.CurrentAsync(CancellationToken.None)).Stopped);

        await policy.StopAsync("operator", "now", CancellationToken.None);

        // Somebody reaching for a stop is not in a mood to wait for a process to notice.
        Assert.True((await policy.CurrentAsync(CancellationToken.None)).Stopped);
    }
}
