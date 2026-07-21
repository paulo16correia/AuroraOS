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
}
