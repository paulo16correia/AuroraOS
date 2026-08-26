using Aurora.Adapters.Persistence;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

public sealed class ApprovalStoreTests
{
    private static readonly Principal Caller = new("c1", "u1");

    private static SqliteApprovalStore NewStore(SqliteTestDb db) =>
        new(db.Factory, new TestClock(DateTimeOffset.UnixEpoch));

    [Fact]
    public async Task Evaluate_NoRecord_CreatesPending_ThenIsIdempotentOnRetry()
    {
        using var db = new SqliteTestDb();
        var store = NewStore(db);

        var first = await store.EvaluateAsync(Caller, "vault.write", "scope-1", CancellationToken.None);
        Assert.Equal(ApprovalOutcome.Pending, first.Outcome);

        var second = await store.EvaluateAsync(Caller, "vault.write", "scope-1", CancellationToken.None);
        Assert.Equal(ApprovalOutcome.Pending, second.Outcome);
        Assert.Equal(first.ApprovalId, second.ApprovalId); // same live PENDING row, not a duplicate
    }

    [Fact]
    public async Task Decide_Approve_ThenEvaluateConsumesOnce()
    {
        using var db = new SqliteTestDb();
        var store = NewStore(db);

        var pending = await store.EvaluateAsync(Caller, "vault.write", "scope-1", CancellationToken.None);
        var decide = await store.DecideAsync(Caller, pending.ApprovalId, approve: true, CancellationToken.None);
        Assert.Equal(ApprovalDecideOutcome.Decided, decide.Outcome);
        Assert.Equal(ApprovalStatus.Approved, decide.Record!.Status);

        var consumed = await store.EvaluateAsync(Caller, "vault.write", "scope-1", CancellationToken.None);
        Assert.Equal(ApprovalOutcome.Consumed, consumed.Outcome);
        Assert.Equal(pending.ApprovalId, consumed.ApprovalId);

        // One-time use: nothing live remains for this scope, so a fresh PENDING is created.
        var again = await store.EvaluateAsync(Caller, "vault.write", "scope-1", CancellationToken.None);
        Assert.Equal(ApprovalOutcome.Pending, again.Outcome);
        Assert.NotEqual(pending.ApprovalId, again.ApprovalId);
    }

    [Fact]
    public async Task Decide_Reject_StaysDenied_ForTheSameScope()
    {
        using var db = new SqliteTestDb();
        var store = NewStore(db);

        var pending = await store.EvaluateAsync(Caller, "vault.write", "scope-1", CancellationToken.None);
        await store.DecideAsync(Caller, pending.ApprovalId, approve: false, CancellationToken.None);

        var evaluated = await store.EvaluateAsync(Caller, "vault.write", "scope-1", CancellationToken.None);
        Assert.Equal(ApprovalOutcome.Rejected, evaluated.Outcome);

        // Rejections are a deliberate decision, not time-bound: re-evaluating repeats the rejection.
        var again = await store.EvaluateAsync(Caller, "vault.write", "scope-1", CancellationToken.None);
        Assert.Equal(ApprovalOutcome.Rejected, again.Outcome);
        Assert.Equal(evaluated.ApprovalId, again.ApprovalId);
    }

    [Fact]
    public async Task Decide_UnknownId_ReturnsNotFound()
    {
        using var db = new SqliteTestDb();
        var store = NewStore(db);

        var result = await store.DecideAsync(Caller, "does-not-exist", approve: true, CancellationToken.None);
        Assert.Equal(ApprovalDecideOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Decide_AlreadyDecided_ReturnsNotPending()
    {
        using var db = new SqliteTestDb();
        var store = NewStore(db);

        var pending = await store.EvaluateAsync(Caller, "vault.write", "scope-1", CancellationToken.None);
        await store.DecideAsync(Caller, pending.ApprovalId, approve: true, CancellationToken.None);

        var again = await store.DecideAsync(Caller, pending.ApprovalId, approve: true, CancellationToken.None);
        Assert.Equal(ApprovalDecideOutcome.NotPending, again.Outcome);
    }

    [Fact]
    public async Task Decide_WrongPrincipal_ReturnsNotFound()
    {
        using var db = new SqliteTestDb();
        var store = NewStore(db);

        var pending = await store.EvaluateAsync(Caller, "vault.write", "scope-1", CancellationToken.None);
        var stranger = new Principal("c2", "u2");

        var result = await store.DecideAsync(stranger, pending.ApprovalId, approve: true, CancellationToken.None);
        Assert.Equal(ApprovalDecideOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task DifferentScopeHash_IsIndependent()
    {
        using var db = new SqliteTestDb();
        var store = NewStore(db);

        var a = await store.EvaluateAsync(Caller, "vault.write", "scope-A", CancellationToken.None);
        var b = await store.EvaluateAsync(Caller, "vault.write", "scope-B", CancellationToken.None);

        Assert.NotEqual(a.ApprovalId, b.ApprovalId);

        await store.DecideAsync(Caller, a.ApprovalId, approve: true, CancellationToken.None);

        // Approving scope-A must not affect the independent pending request for scope-B.
        var stillPendingB = await store.EvaluateAsync(Caller, "vault.write", "scope-B", CancellationToken.None);
        Assert.Equal(ApprovalOutcome.Pending, stillPendingB.Outcome);
        Assert.Equal(b.ApprovalId, stillPendingB.ApprovalId);
    }

    [Fact]
    public async Task AnApprovalThatExpiredIsNotUsableAndANewOneIsAsked()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var store = new SqliteApprovalStore(db.Factory, clock);

        var pending = await store.EvaluateAsync(Caller, "vault.write", "scope-1", CancellationToken.None);
        await store.DecideAsync(Caller, pending.ApprovalId, approve: true, CancellationToken.None);

        // Long enough for any window: an approval is a person agreeing to something now, and two
        // days later they are not in the room.
        clock.UtcNow = DateTimeOffset.UnixEpoch.AddDays(2);

        var stale = await store.EvaluateAsync(Caller, "vault.write", "scope-1", CancellationToken.None);

        // Not consumed, and not silently reused: what comes back is a fresh pending record,
        // because the question has to be asked again rather than assumed answered.
        Assert.Equal(ApprovalOutcome.Pending, stale.Outcome);
        Assert.NotEqual(pending.ApprovalId, stale.ApprovalId);
    }

    [Fact]
    public async Task APendingApprovalThatExpiredIsReplacedRatherThanRevived()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var store = new SqliteApprovalStore(db.Factory, clock);

        var first = await store.EvaluateAsync(Caller, "vault.write", "scope-1", CancellationToken.None);
        clock.UtcNow = DateTimeOffset.UnixEpoch.AddDays(2);

        var second = await store.EvaluateAsync(Caller, "vault.write", "scope-1", CancellationToken.None);
        Assert.NotEqual(first.ApprovalId, second.ApprovalId);

        // And deciding the dead one does not bring it back. A prompt somebody answered a day late
        // is not consent to something happening now.
        await store.DecideAsync(Caller, first.ApprovalId, approve: true, CancellationToken.None);

        var after = await store.EvaluateAsync(Caller, "vault.write", "scope-1", CancellationToken.None);
        Assert.NotEqual(ApprovalOutcome.Consumed, after.Outcome);
    }
}
