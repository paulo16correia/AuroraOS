using Aurora.Adapters.WorkItems;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// RFC 02: one unit of work per thing that arrived, and at most one active per idempotency key.
/// </summary>
/// <remarks>
/// <c>CognitiveCycle.WorkItemId</c> and <c>tool_call.work_item_id</c> referenced this for a long
/// time before it existed. What the column actually held was a subject reference — the same value
/// for every call of the same capability — so the question it looked like it could answer, "which
/// cycles belong to this work", it could not.
/// </remarks>
public sealed class WorkItemTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");

    private static SqliteWorkItemService New(SqliteTestDb db) => new(db.Factory, new TestClock(Now));

    private static Task<WorkItem> HandleAsync(
        SqliteWorkItemService service, string key = "k1", string correlation = "corr-1") =>
        service.HandleAsync(correlation, key, null, null, null, Ct);

    [Fact]
    public async Task ARepeatedRequestJoinsTheWorkAlreadyInFlight()
    {
        using var db = new SqliteTestDb();
        var service = New(db);

        WorkItem first = await HandleAsync(service);
        WorkItem again = await HandleAsync(service, correlation: "corr-2");

        // Rule 1: at most one active work item per key. Not an error — the right answer to a
        // repeated request is the work already going on.
        Assert.Equal(first.Id, again.Id);
        Assert.Single(await service.ActiveAsync(Ct));
    }

    [Fact]
    public async Task TheSameKeyStartsAgainOnceTheFirstIsFinished()
    {
        using var db = new SqliteTestDb();
        var service = New(db);

        WorkItem first = await HandleAsync(service);
        await service.AdvanceAsync(first.Id, WorkItemStatus.Contextualized, Ct);
        await service.AdvanceAsync(first.Id, WorkItemStatus.Deliberating, Ct);
        await service.AdvanceAsync(first.Id, WorkItemStatus.Completed, Ct);

        WorkItem second = await HandleAsync(service);

        // "One active", not "one ever": asking the same thing again tomorrow is a new unit of work.
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(WorkItemStatus.Received, second.Status);
    }

    [Fact]
    public async Task WorkWithoutAKeyIsRefused()
    {
        using var db = new SqliteTestDb();
        var service = New(db);

        // Without a key rule 1 has nothing to count, and every repeat becomes a new unit of work
        // indistinguishable from the first.
        await Assert.ThrowsAsync<WorkItemException>(() => HandleAsync(service, key: "   "));
    }

    [Fact]
    public async Task AWorkItemDoesNotSkipBackwardsOrOutOfATerminalState()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        WorkItem item = await HandleAsync(service);

        await Assert.ThrowsAsync<WorkItemException>(
            () => service.AdvanceAsync(item.Id, WorkItemStatus.Executing, Ct));

        await service.AdvanceAsync(item.Id, WorkItemStatus.Contextualized, Ct);
        await service.AdvanceAsync(item.Id, WorkItemStatus.Deliberating, Ct);
        await service.AdvanceAsync(item.Id, WorkItemStatus.Failed, Ct);

        // Terminal is terminal, and a failure that could be walked back out of is not a record of
        // what happened.
        await Assert.ThrowsAsync<WorkItemException>(
            () => service.AdvanceAsync(item.Id, WorkItemStatus.Executing, Ct));
    }

    [Fact]
    public async Task CancellingNamesWhoCancelled()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        WorkItem item = await HandleAsync(service);

        await Assert.ThrowsAsync<WorkItemException>(() => service.CancelAsync(item.Id, "  ", Ct));

        WorkItem cancelled = await service.CancelAsync(item.Id, "owner", Ct);

        Assert.Equal(WorkItemStatus.Cancelled, cancelled.Status);
        Assert.Equal("owner", cancelled.CancelledBy);
        Assert.Empty(await service.ActiveAsync(Ct));

        await Assert.ThrowsAsync<WorkItemException>(() => service.CancelAsync(item.Id, "owner", Ct));
    }

    [Fact]
    public async Task AWaitingApprovalItemIsStillActive()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        WorkItem item = await HandleAsync(service);

        await service.AdvanceAsync(item.Id, WorkItemStatus.Contextualized, Ct);
        await service.AdvanceAsync(item.Id, WorkItemStatus.Deliberating, Ct);
        await service.AdvanceAsync(item.Id, WorkItemStatus.WaitingApproval, Ct);

        // Waiting on a person is still work in flight. Treating it as finished would let a second
        // identical request start beside the one already waiting to be approved.
        Assert.Single(await service.ActiveAsync(Ct));
        Assert.Equal(item.Id, (await HandleAsync(service)).Id);
    }
}
