using Aurora.Adapters.Cognition;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>Conformance tests for RFC 021.</summary>
public sealed class CognitiveCycleTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");

    private static SqliteCognitiveCycle New(SqliteTestDb db) =>
        new(db.Factory, new TestClock(Now));

    private static CycleIngress Ingress() =>
        new("work/1", "mcp/call-1", "mcp/session-1");

    private static async Task<CognitiveCycle> StartedAsync(SqliteCognitiveCycle cycle) =>
        await cycle.RunAsync(Ingress(), Ct);

    /// <summary>Runs every stage from Perception up to and including <paramref name="upTo"/>.</summary>
    private static async Task ReachAsync(SqliteCognitiveCycle cycle, string id, string upTo)
    {
        foreach (var stage in CycleStage.Order)
        {
            await cycle.AdvanceAsync(id, stage, [], [], null, Ct);
            if (stage == upTo)
            {
                return;
            }
        }
    }

    [Fact]
    public async Task ACycleStartsAtPerception()
    {
        using var db = new SqliteTestDb();

        CognitiveCycle cycle = await StartedAsync(New(db));

        Assert.Equal(CycleStage.Perception, cycle.Stage);
        Assert.Equal(CycleStatus.Running, cycle.Status);
    }

    // ---- rule 1: no stage is jumped over ----

    [Fact]
    public async Task AStageCannotBeJumpedOver()
    {
        using var db = new SqliteTestDb();
        var cycle = New(db);
        CognitiveCycle started = await StartedAsync(cycle);
        await cycle.AdvanceAsync(started.Id, CycleStage.Perception, [], [], null, Ct);

        CognitiveCycleException error = await Assert.ThrowsAsync<CognitiveCycleException>(
            () => cycle.AdvanceAsync(started.Id, CycleStage.Decision, [], [], null, Ct));

        Assert.Contains(CycleStage.Attention, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStageMayBeOmittedWithAReason()
    {
        using var db = new SqliteTestDb();
        var cycle = New(db);
        CognitiveCycle started = await StartedAsync(cycle);
        await cycle.AdvanceAsync(started.Id, CycleStage.Perception, [], [], null, Ct);

        // Low-value ingress: attention may decline a full cycle, and the reason is the record.
        await cycle.OmitAsync(started.Id, CycleStage.Attention, "ingress below attention threshold", Ct);
        await cycle.OmitAsync(started.Id, CycleStage.WorkingMemory, "nothing to hold", Ct);
        await cycle.OmitAsync(started.Id, CycleStage.Memory, "no lookup needed", Ct);
        await cycle.OmitAsync(started.Id, CycleStage.WorldModel, "no facts needed", Ct);
        await cycle.OmitAsync(started.Id, CycleStage.Planner, "no goal", Ct);

        CycleStageRecord decision = await cycle.AdvanceAsync(started.Id, CycleStage.Decision, [], [], null, Ct);

        Assert.Equal(StageStatus.Done, decision.Status);

        IReadOnlyList<CycleStageRecord> stages = await cycle.StagesAsync(started.Id, Ct);
        Assert.Contains(stages, s => s.Stage == CycleStage.Attention && s.Status == StageStatus.Omitted);
        Assert.All(
            stages.Where(s => s.Status == StageStatus.Omitted),
            s => Assert.False(string.IsNullOrWhiteSpace(s.Note)));
    }

    [Fact]
    public async Task AnOmissionWithoutAReasonIsRefused()
    {
        using var db = new SqliteTestDb();
        var cycle = New(db);
        CognitiveCycle started = await StartedAsync(cycle);

        await Assert.ThrowsAsync<CognitiveCycleException>(
            () => cycle.OmitAsync(started.Id, CycleStage.Perception, "   ", Ct));
    }

    // ---- rule 2: no stateful result before Decision and Policy ----

    [Fact]
    public async Task AResultCarryingStateCannotBeProducedBeforeDecisionAndPolicy()
    {
        using var db = new SqliteTestDb();
        var cycle = New(db);
        CognitiveCycle started = await StartedAsync(cycle);
        await ReachAsync(cycle, started.Id, CycleStage.Planner);

        CognitiveCycleException error = await Assert.ThrowsAsync<CognitiveCycleException>(
            () => cycle.CompleteAsync(started.Id, carriesPersistentStateOrExecution: true, "wrote a note", Ct));

        Assert.Contains(CycleStage.Decision, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APlainAnswerMayCompleteWithoutDecisionAndPolicy()
    {
        using var db = new SqliteTestDb();
        var cycle = New(db);
        CognitiveCycle started = await StartedAsync(cycle);
        await ReachAsync(cycle, started.Id, CycleStage.Planner);

        CycleResult result = await cycle.CompleteAsync(
            started.Id, carriesPersistentStateOrExecution: false, "answered from context", Ct);

        Assert.Equal(CycleStatus.Completed, result.Status);
    }

    [Fact]
    public async Task AResultCarryingStateCompletesOnceDecisionAndPolicyHaveRun()
    {
        using var db = new SqliteTestDb();
        var cycle = New(db);
        CognitiveCycle started = await StartedAsync(cycle);
        await ReachAsync(cycle, started.Id, CycleStage.Policy);

        CycleResult result = await cycle.CompleteAsync(
            started.Id, carriesPersistentStateOrExecution: true, "recorded a memory", Ct);

        Assert.True(result.CarriesPersistentStateOrExecution);
    }

    // ---- rule 3: no effect without policy ----

    [Fact]
    public async Task AnEffectCannotBeMarkedBeforePolicyHasRun()
    {
        using var db = new SqliteTestDb();
        var cycle = New(db);
        CognitiveCycle started = await StartedAsync(cycle);
        await ReachAsync(cycle, started.Id, CycleStage.Decision);

        await Assert.ThrowsAsync<CognitiveCycleException>(() => cycle.MarkExecutedAsync(
            started.Id, policyAllowed: true, approvalSatisfied: true, Ct));
    }

    [Fact]
    public async Task AnEffectIsRefusedWhenPolicyDidNotAllowIt()
    {
        using var db = new SqliteTestDb();
        var cycle = New(db);
        CognitiveCycle started = await StartedAsync(cycle);
        await ReachAsync(cycle, started.Id, CycleStage.Policy);

        await Assert.ThrowsAsync<CognitiveCycleException>(() => cycle.MarkExecutedAsync(
            started.Id, policyAllowed: false, approvalSatisfied: true, Ct));
    }

    [Fact]
    public async Task AnEffectIsRefusedWhenTheApprovalIsNotSatisfied()
    {
        using var db = new SqliteTestDb();
        var cycle = New(db);
        CognitiveCycle started = await StartedAsync(cycle);
        await ReachAsync(cycle, started.Id, CycleStage.Policy);

        await Assert.ThrowsAsync<CognitiveCycleException>(() => cycle.MarkExecutedAsync(
            started.Id, policyAllowed: true, approvalSatisfied: false, Ct));
    }

    // ---- rule 5: observation and reflection after any execution ----

    [Fact]
    public async Task AnExecutedCycleCannotCloseWithoutObservationAndReflection()
    {
        using var db = new SqliteTestDb();
        var cycle = New(db);
        CognitiveCycle started = await StartedAsync(cycle);
        await ReachAsync(cycle, started.Id, CycleStage.Executor);
        await cycle.MarkExecutedAsync(started.Id, true, true, Ct);

        CognitiveCycleException error = await Assert.ThrowsAsync<CognitiveCycleException>(
            () => cycle.CompleteAsync(started.Id, true, "sent the email", Ct));

        Assert.Contains(CycleStage.Observation, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReflectionConcludingNothingStillCounts()
    {
        using var db = new SqliteTestDb();
        var cycle = New(db);
        CognitiveCycle started = await StartedAsync(cycle);
        await ReachAsync(cycle, started.Id, CycleStage.Executor);
        await cycle.MarkExecutedAsync(started.Id, true, true, Ct);
        await cycle.AdvanceAsync(started.Id, CycleStage.Observation, [], ["obs/1"], null, Ct);

        // "No learning" is a conclusion, and recording it is what rule 5 asks for.
        await cycle.AdvanceAsync(started.Id, CycleStage.Reflection, [], [], null, Ct);

        CycleResult result = await cycle.CompleteAsync(started.Id, true, "sent the email", Ct);

        Assert.Equal(CycleStatus.Completed, result.Status);
    }

    // ---- rule 4 and the interruption cases ----

    [Fact]
    public async Task ACycleMayWaitAndResume()
    {
        using var db = new SqliteTestDb();
        var cycle = New(db);
        CognitiveCycle started = await StartedAsync(cycle);
        await ReachAsync(cycle, started.Id, CycleStage.Decision);

        CognitiveCycle waiting = await cycle.WaitAsync(started.Id, "awaiting approval", Ct);
        Assert.Equal(CycleStatus.Waiting, waiting.Status);

        CognitiveCycle resumed = await cycle.ResumeAsync(started.Id, "approval granted", Ct);
        Assert.Equal(CycleStatus.Running, resumed.Status);

        // The stage survived the pause, which is what "persist the current stage" means.
        Assert.Equal(CycleStage.Decision, (await cycle.GetAsync(started.Id, Ct))!.Stage);
    }

    [Fact]
    public async Task ResumingIsIdempotent()
    {
        using var db = new SqliteTestDb();
        var cycle = New(db);
        CognitiveCycle started = await StartedAsync(cycle);

        await cycle.ResumeAsync(started.Id, "spurious trigger", Ct);
        CognitiveCycle again = await cycle.ResumeAsync(started.Id, "spurious trigger", Ct);

        Assert.Equal(CycleStatus.Running, again.Status);
    }

    [Fact]
    public async Task AFinishedCycleRecordsNothingFurther()
    {
        using var db = new SqliteTestDb();
        var cycle = New(db);
        CognitiveCycle started = await StartedAsync(cycle);
        await cycle.AdvanceAsync(started.Id, CycleStage.Perception, [], [], null, Ct);
        await cycle.FailAsync(started.Id, "invalid MCP request", Ct);

        await Assert.ThrowsAsync<CognitiveCycleException>(
            () => cycle.AdvanceAsync(started.Id, CycleStage.Attention, [], [], null, Ct));

        await Assert.ThrowsAsync<CognitiveCycleException>(
            () => cycle.ResumeAsync(started.Id, "try again", Ct));
    }

    [Fact]
    public async Task TheStageRecordShowsWhetherDecisionWasReached()
    {
        using var db = new SqliteTestDb();
        var cycle = New(db);
        CognitiveCycle started = await StartedAsync(cycle);
        await ReachAsync(cycle, started.Id, CycleStage.Policy);

        IReadOnlyList<CycleStageRecord> stages = await cycle.StagesAsync(started.Id, Ct);

        // The record answers the audit question directly, in the order the RFC lays out.
        Assert.Equal(
            CycleStage.Order.TakeWhile(s => s != CycleStage.Capabilities),
            stages.Select(s => s.Stage));
    }
}
