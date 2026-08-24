using Aurora.Adapters.Lifecycle;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>Conformance tests for RFC 039.</summary>
public sealed class InstanceLifecycleTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Id = "instance-1";

    private static SqliteInstanceLifecycle New(SqliteTestDb db) =>
        new(db.Factory, new TestClock(DateTimeOffset.UnixEpoch));

    private static async Task<SqliteInstanceLifecycle> ReadyAsync(SqliteTestDb db)
    {
        var lifecycle = New(db);
        await lifecycle.GetOrCreateAsync(Id, Ct);
        await lifecycle.TransitionAsync(Id, InstanceState.Bootstrapping, TransitionActor.Kernel, "boot", ct: Ct);
        await lifecycle.TransitionAsync(Id, InstanceState.Recovering, TransitionActor.Kernel, "recover", ct: Ct);
        await lifecycle.TransitionAsync(Id, InstanceState.Ready, TransitionActor.Kernel, "ready", ct: Ct);
        return lifecycle;
    }

    [Fact]
    public async Task ANewInstanceStartsAsCreated()
    {
        using var db = new SqliteTestDb();

        InstanceLifecycle instance = await New(db).GetOrCreateAsync(Id, Ct);

        Assert.Equal(InstanceState.Created, instance.State);
        Assert.Equal(1, instance.Version);
    }

    [Fact]
    public async Task TheBootPathReachesReady()
    {
        using var db = new SqliteTestDb();
        var lifecycle = await ReadyAsync(db);

        Assert.Equal(InstanceState.Ready, (await lifecycle.GetOrCreateAsync(Id, Ct)).State);
    }

    [Fact]
    public async Task BootMayNotSkipRecovering()
    {
        using var db = new SqliteTestDb();
        var lifecycle = New(db);
        await lifecycle.GetOrCreateAsync(Id, Ct);
        await lifecycle.TransitionAsync(Id, InstanceState.Bootstrapping, TransitionActor.Kernel, "boot", ct: Ct);

        // RFC 039: a booting instance passes through RECOVERING; it is not declared READY directly.
        TransitionResult result = await lifecycle.TransitionAsync(
            Id, InstanceState.Ready, TransitionActor.Kernel, "skip", ct: Ct);

        Assert.False(result.Ok);
        Assert.Equal(TransitionRefusal.IllegalTransition, result.Refusal);
    }

    [Fact]
    public async Task OnlyTheKernelMayTransition()
    {
        using var db = new SqliteTestDb();
        var lifecycle = New(db);
        await lifecycle.GetOrCreateAsync(Id, Ct);

        // Rule 4: the Mind proposes and never changes the lifecycle itself.
        TransitionResult result = await lifecycle.TransitionAsync(
            Id, InstanceState.Bootstrapping, TransitionActor.Mind, "let me", ct: Ct);

        Assert.False(result.Ok);
        Assert.Equal(TransitionRefusal.NotAuthorised, result.Refusal);
        Assert.Equal(InstanceState.Created, (await lifecycle.GetOrCreateAsync(Id, Ct)).State);
    }

    [Fact]
    public async Task AProposalIsRecordedButChangesNothing()
    {
        using var db = new SqliteTestDb();
        var lifecycle = await ReadyAsync(db);

        LifecycleProposal proposal = await lifecycle.ProposeAsync(Id, InstanceState.Paused, "I am idle", Ct);

        Assert.Equal(InstanceState.Paused, proposal.TargetState);
        Assert.Equal(InstanceState.Ready, (await lifecycle.GetOrCreateAsync(Id, Ct)).State);
    }

    [Fact]
    public async Task AbruptFailureIsObservedAsRecovering_FromAnyLiveState()
    {
        using var db = new SqliteTestDb();
        var lifecycle = await ReadyAsync(db);
        await lifecycle.TransitionAsync(Id, InstanceState.Executing, TransitionActor.Kernel, "work", ct: Ct);

        // The process died mid-execution; on boot the Kernel reconciles before reactivating.
        TransitionResult result = await lifecycle.TransitionAsync(
            Id, InstanceState.Recovering, TransitionActor.Kernel, "restart after crash", ct: Ct);

        Assert.True(result.Ok);
        Assert.Equal(InstanceState.Recovering, result.Lifecycle!.State);
    }

    [Fact]
    public async Task StoppingWithoutAVerifiedSnapshotIsRefused()
    {
        using var db = new SqliteTestDb();
        var lifecycle = await ReadyAsync(db);
        await lifecycle.TransitionAsync(Id, InstanceState.ShuttingDown, TransitionActor.Kernel, "bye", ct: Ct);

        TransitionResult result = await lifecycle.TransitionAsync(
            Id, InstanceState.Stopped, TransitionActor.Kernel, "bye", ct: Ct);

        Assert.False(result.Ok);
        Assert.Equal(TransitionRefusal.StopWithoutSnapshotOrEmergency, result.Refusal);
    }

    [Fact]
    public async Task StoppingIsAllowedWithAVerifiedSnapshot()
    {
        using var db = new SqliteTestDb();
        var lifecycle = await ReadyAsync(db);
        await lifecycle.SetVerifiedSnapshotAsync(Id, "snapshot-1", Ct);
        await lifecycle.TransitionAsync(Id, InstanceState.ShuttingDown, TransitionActor.Kernel, "bye", ct: Ct);

        Assert.True((await lifecycle.TransitionAsync(
            Id, InstanceState.Stopped, TransitionActor.Kernel, "bye", ct: Ct)).Ok);
    }

    [Fact]
    public async Task StoppingIsAllowedInAnAuditedEmergency()
    {
        using var db = new SqliteTestDb();
        var lifecycle = await ReadyAsync(db);
        await lifecycle.TransitionAsync(Id, InstanceState.ShuttingDown, TransitionActor.Kernel, "fire", ct: Ct);

        TransitionResult result = await lifecycle.TransitionAsync(
            Id, InstanceState.Stopped, TransitionActor.Kernel, "disk failing", emergency: true, ct: Ct);

        Assert.True(result.Ok);
        Assert.Equal("disk failing", result.Lifecycle!.Reason);
    }

    [Fact]
    public async Task BackingUpRequiresWorkToBeDrained()
    {
        using var db = new SqliteTestDb();
        var lifecycle = await ReadyAsync(db);
        await lifecycle.TransitionAsync(Id, InstanceState.Waiting, TransitionActor.Kernel, "idle", ct: Ct);

        using (var connection = db.Factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE instance_lifecycle SET active_cycle_refs = 'cycle-1';";
            command.ExecuteNonQuery();
        }

        TransitionResult result = await lifecycle.TransitionAsync(
            Id, InstanceState.BackingUp, TransitionActor.Kernel, "nightly", ct: Ct);

        Assert.False(result.Ok);
        Assert.Equal(TransitionRefusal.DrainRequired, result.Refusal);
    }

    [Fact]
    public async Task PausedPreventsNewEffects()
    {
        using var db = new SqliteTestDb();
        var lifecycle = await ReadyAsync(db);
        await lifecycle.TransitionAsync(Id, InstanceState.Waiting, TransitionActor.Kernel, "idle", ct: Ct);
        await lifecycle.TransitionAsync(Id, InstanceState.Paused, TransitionActor.Kernel, "operator", ct: Ct);

        Assert.False(InstanceState.AllowsNewEffects(
            (await lifecycle.GetOrCreateAsync(Id, Ct)).State));
    }

    [Fact]
    public async Task ResumeReturnsAPausedInstanceToReady()
    {
        using var db = new SqliteTestDb();
        var lifecycle = await ReadyAsync(db);
        await lifecycle.TransitionAsync(Id, InstanceState.Waiting, TransitionActor.Kernel, "idle", ct: Ct);
        await lifecycle.TransitionAsync(Id, InstanceState.Paused, TransitionActor.Kernel, "operator", ct: Ct);

        TransitionResult result = await lifecycle.ResumeAsync(Id, "operator resumed", Ct);

        Assert.True(result.Ok);
        Assert.Equal(InstanceState.Ready, result.Lifecycle!.State);
        Assert.True(InstanceState.AllowsNewEffects(result.Lifecycle.State));
    }

    [Fact]
    public async Task ARetiredInstanceGoesNowhere()
    {
        using var db = new SqliteTestDb();
        var lifecycle = await ReadyAsync(db);
        await lifecycle.SetVerifiedSnapshotAsync(Id, "snapshot-1", Ct);
        await lifecycle.TransitionAsync(Id, InstanceState.ShuttingDown, TransitionActor.Kernel, "bye", ct: Ct);
        await lifecycle.TransitionAsync(Id, InstanceState.Stopped, TransitionActor.Kernel, "bye", ct: Ct);
        await lifecycle.TransitionAsync(Id, InstanceState.Retired, TransitionActor.Kernel, "done", ct: Ct);

        TransitionResult result = await lifecycle.TransitionAsync(
            Id, InstanceState.Ready, TransitionActor.Kernel, "back please", ct: Ct);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task ShutdownPlanReportsPendingWorkAndSnapshotState()
    {
        using var db = new SqliteTestDb();
        var lifecycle = await ReadyAsync(db);
        await lifecycle.SetPendingActionsAsync(Id, ["call-1", "call-2"], Ct);

        ShutdownPlan plan = await lifecycle.PrepareShutdownAsync(Id, Ct);

        Assert.False(plan.HasVerifiedSnapshot);
        Assert.Equal(2, plan.PendingActionRefs.Count);
        Assert.Contains(plan.Steps, s => s.Contains("reconcile", StringComparison.Ordinal));
    }
}
