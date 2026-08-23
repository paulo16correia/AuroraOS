using Aurora.Adapters.Persistence;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

public sealed class ConsentSessionStoreTests
{
    private static readonly Principal Caller = new("c1", "u1");
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    private static SqliteConsentSessionStore Store(
        SqliteTestDb db,
        DateTimeOffset? now = null,
        string bootId = "boot-1",
        string policyVersion = "pv-1",
        int maxActions = 50,
        TimeSpan? lifetime = null) =>
        new(db.Factory,
            new TestClock(now ?? Start),
            new FakeServerIdentity(bootId),
            new VersionedFakePolicy(true, policyVersion),
            new ConsentSessionOptions(lifetime ?? TimeSpan.FromMinutes(15), maxActions));

    [Fact]
    public async Task TryUse_WithoutASession_ReportsNone()
    {
        using var db = new SqliteTestDb();

        var use = await Store(db).TryUseAsync(Caller, CancellationToken.None);

        Assert.Equal(ConsentSessionUseOutcome.None, use.Outcome);
    }

    [Fact]
    public async Task OpenThenUse_CoversTheRequest()
    {
        using var db = new SqliteTestDb();
        var store = Store(db);

        var session = await store.OpenAsync(Caller, CancellationToken.None);
        var use = await store.TryUseAsync(Caller, CancellationToken.None);

        Assert.Equal(ConsentSessionUseOutcome.Used, use.Outcome);
        Assert.Equal(session.SessionId, use.SessionId);
    }

    [Fact]
    public async Task Open_ReusesTheLiveSessionInsteadOfStackingBudgets()
    {
        using var db = new SqliteTestDb();
        var store = Store(db);

        var first = await store.OpenAsync(Caller, CancellationToken.None);
        var second = await store.OpenAsync(Caller, CancellationToken.None);

        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Equal(1, await store.CountActiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Budget_IsSpentAndThenExhausted()
    {
        using var db = new SqliteTestDb();
        var store = Store(db, maxActions: 2);
        await store.OpenAsync(Caller, CancellationToken.None);

        Assert.Equal(ConsentSessionUseOutcome.Used, (await store.TryUseAsync(Caller, CancellationToken.None)).Outcome);
        Assert.Equal(ConsentSessionUseOutcome.Used, (await store.TryUseAsync(Caller, CancellationToken.None)).Outcome);

        // The ceiling is the point: a session is not an unlimited licence.
        Assert.Equal(ConsentSessionUseOutcome.None, (await store.TryUseAsync(Caller, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Expiry_EndsTheSession()
    {
        using var db = new SqliteTestDb();
        await Store(db, lifetime: TimeSpan.FromMinutes(15)).OpenAsync(Caller, CancellationToken.None);

        var later = Store(db, now: Start.AddMinutes(16));

        Assert.Equal(ConsentSessionUseOutcome.None, (await later.TryUseAsync(Caller, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Restart_InvalidatesEverySession()
    {
        using var db = new SqliteTestDb();
        await Store(db, bootId: "boot-1").OpenAsync(Caller, CancellationToken.None);

        // A new boot id is how "grants do not survive a restart" is enforced: nothing matches.
        var afterRestart = Store(db, bootId: "boot-2");

        Assert.Equal(ConsentSessionUseOutcome.None, (await afterRestart.TryUseAsync(Caller, CancellationToken.None)).Outcome);
        Assert.Equal(0, await afterRestart.CountActiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PolicyChange_InvalidatesEverySession()
    {
        using var db = new SqliteTestDb();
        await Store(db, policyVersion: "pv-1").OpenAsync(Caller, CancellationToken.None);

        // A grant issued under the old rules must not survive a tightening of the new ones.
        var afterChange = Store(db, policyVersion: "pv-2");

        Assert.Equal(ConsentSessionUseOutcome.None, (await afterChange.TryUseAsync(Caller, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task AnotherPrincipalIsNotCovered()
    {
        using var db = new SqliteTestDb();
        var store = Store(db);
        await store.OpenAsync(Caller, CancellationToken.None);

        var other = await store.TryUseAsync(new Principal("c2", "u2"), CancellationToken.None);

        Assert.Equal(ConsentSessionUseOutcome.None, other.Outcome);
    }

    [Fact]
    public async Task KillSwitch_RevokesEverything_IncludingEarlierBoots()
    {
        using var db = new SqliteTestDb();
        await Store(db, bootId: "boot-old").OpenAsync(Caller, CancellationToken.None);
        var current = Store(db, bootId: "boot-now");
        await current.OpenAsync(Caller, CancellationToken.None);

        var revoked = await current.RevokeAllAsync(CancellationToken.None);

        // Two rows, including the one from a previous run: an operator hitting the kill switch
        // should not have to reason about restarts.
        Assert.Equal(2, revoked);
        Assert.Equal(ConsentSessionUseOutcome.None, (await current.TryUseAsync(Caller, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task ConcurrentUse_NeverOverspendsTheBudget()
    {
        using var db = new SqliteTestDb();
        var store = Store(db, maxActions: 5);
        await store.OpenAsync(Caller, CancellationToken.None);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => store.TryUseAsync(Caller, CancellationToken.None)));

        Assert.Equal(5, results.Count(r => r.Outcome == ConsentSessionUseOutcome.Used));
    }
}
