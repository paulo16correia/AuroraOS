using Aurora.Adapters.Persistence;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

public sealed class IdempotencyStoreTests
{
    private static readonly Principal Caller = new("c1", "u1");

    private static SqliteIdempotencyStore NewStore(SqliteTestDb db) =>
        new(db.Factory, new TestClock(DateTimeOffset.UnixEpoch));

    [Fact]
    public async Task Begin_IsFresh_ThenInProgress_ThenConflict_ThenReplay()
    {
        using var db = new SqliteTestDb();
        var store = NewStore(db);

        var begin = await store.BeginAsync(Caller, "k1", "hashA", CancellationToken.None);
        Assert.Equal(IdempotencyDisposition.Begin, begin.Disposition);

        var again = await store.BeginAsync(Caller, "k1", "hashA", CancellationToken.None);
        Assert.Equal(IdempotencyDisposition.InProgress, again.Disposition);

        var conflict = await store.BeginAsync(Caller, "k1", "hashB", CancellationToken.None);
        Assert.Equal(IdempotencyDisposition.Conflict, conflict.Disposition);

        await store.CompleteAsync(Caller, "k1", IdempotencyState.Completed, """{"ok":true}""", CancellationToken.None);
        var replay = await store.BeginAsync(Caller, "k1", "hashA", CancellationToken.None);
        Assert.Equal(IdempotencyDisposition.ReplayCompleted, replay.Disposition);
        Assert.Equal("""{"ok":true}""", replay.StoredResultJson);
    }

    [Fact]
    public async Task MarkExecuting_ThenFailed_ReplaysFailure()
    {
        using var db = new SqliteTestDb();
        var store = NewStore(db);

        await store.BeginAsync(Caller, "k1", "h", CancellationToken.None);
        await store.MarkExecutingAsync(Caller, "k1", CancellationToken.None);

        var midflight = await store.BeginAsync(Caller, "k1", "h", CancellationToken.None);
        Assert.Equal(IdempotencyDisposition.InProgress, midflight.Disposition);

        await store.CompleteAsync(Caller, "k1", IdempotencyState.Failed, """{"error":true}""", CancellationToken.None);
        var replay = await store.BeginAsync(Caller, "k1", "h", CancellationToken.None);
        Assert.Equal(IdempotencyDisposition.ReplayFailed, replay.Disposition);
    }

    [Fact]
    public async Task DifferentPrincipals_SameKey_AreIndependent()
    {
        using var db = new SqliteTestDb();
        var store = NewStore(db);

        var first = await store.BeginAsync(new Principal("c1", "u"), "shared", "h", CancellationToken.None);
        var second = await store.BeginAsync(new Principal("c2", "u"), "shared", "h", CancellationToken.None);

        Assert.Equal(IdempotencyDisposition.Begin, first.Disposition);
        Assert.Equal(IdempotencyDisposition.Begin, second.Disposition);
    }

    [Fact]
    public async Task MarkExecuting_IsCompareAndSet()
    {
        using var db = new SqliteTestDb();
        var store = NewStore(db);

        await store.BeginAsync(Caller, "k", "h", CancellationToken.None);
        Assert.True(await store.MarkExecutingAsync(Caller, "k", CancellationToken.None));  // ACCEPTED -> EXECUTING
        Assert.False(await store.MarkExecutingAsync(Caller, "k", CancellationToken.None)); // no longer ACCEPTED
    }

    // ---- It.3 reconciliation (docs/adr/0007) ----

    private static SqliteIdempotencyStore StoreAt(SqliteTestDb db, DateTimeOffset now) =>
        new(db.Factory, new TestClock(now));

    [Fact]
    public async Task Reconcile_MovesStaleExecutingToUnknown_AndUnwedgesTheKey()
    {
        using var db = new SqliteTestDb();
        var start = DateTimeOffset.UnixEpoch;

        // A request that reached EXECUTING and then died with the process.
        var crashed = StoreAt(db, start);
        await crashed.BeginAsync(Caller, "k1", "hashA", CancellationToken.None);
        Assert.True(await crashed.MarkExecutingAsync(Caller, "k1", CancellationToken.None));

        // Without reconciliation the key stays wedged: EXECUTING is not retryable by design.
        Assert.Equal(
            IdempotencyDisposition.InProgress,
            (await crashed.BeginAsync(Caller, "k1", "hashA", CancellationToken.None)).Disposition);

        var later = StoreAt(db, start.AddHours(1));
        var moved = await later.ReconcileStaleAsync(TimeSpan.FromMinutes(15), CancellationToken.None);

        Assert.Equal(1, moved);
        Assert.Equal(
            IdempotencyDisposition.Unknown,
            (await later.BeginAsync(Caller, "k1", "hashA", CancellationToken.None)).Disposition);
    }

    [Fact]
    public async Task Reconcile_LeavesRecentExecutingAlone()
    {
        using var db = new SqliteTestDb();
        var start = DateTimeOffset.UnixEpoch;

        var store = StoreAt(db, start);
        await store.BeginAsync(Caller, "k1", "hashA", CancellationToken.None);
        await store.MarkExecutingAsync(Caller, "k1", CancellationToken.None);

        // A slow but live execution must never be declared indeterminate underneath itself.
        var soon = StoreAt(db, start.AddMinutes(5));
        var moved = await soon.ReconcileStaleAsync(TimeSpan.FromMinutes(15), CancellationToken.None);

        Assert.Equal(0, moved);
        Assert.Equal(
            IdempotencyDisposition.InProgress,
            (await soon.BeginAsync(Caller, "k1", "hashA", CancellationToken.None)).Disposition);
    }

    [Fact]
    public async Task Reconcile_IgnoresReservationsThatNeverStartedExecuting()
    {
        using var db = new SqliteTestDb();
        var start = DateTimeOffset.UnixEpoch;

        // ACCEPTED means no effect was attempted, so it is abandonable rather than indeterminate.
        var store = StoreAt(db, start);
        await store.BeginAsync(Caller, "k1", "hashA", CancellationToken.None);

        var later = StoreAt(db, start.AddHours(1));

        Assert.Equal(0, await later.ReconcileStaleAsync(TimeSpan.FromMinutes(15), CancellationToken.None));
    }

    [Fact]
    public async Task Reconcile_IsIdempotent()
    {
        using var db = new SqliteTestDb();
        var start = DateTimeOffset.UnixEpoch;
        var store = StoreAt(db, start);
        await store.BeginAsync(Caller, "k1", "hashA", CancellationToken.None);
        await store.MarkExecutingAsync(Caller, "k1", CancellationToken.None);

        var later = StoreAt(db, start.AddHours(1));
        Assert.Equal(1, await later.ReconcileStaleAsync(TimeSpan.FromMinutes(15), CancellationToken.None));
        Assert.Equal(0, await later.ReconcileStaleAsync(TimeSpan.FromMinutes(15), CancellationToken.None));
    }
}
