using System.Text.Json;
using Aurora.Adapters.Cognition;
using Aurora.Adapters.Deliberation;
using Aurora.Adapters.Events;
using Aurora.Adapters.Memories;
using Aurora.Adapters.Observations;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.World;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Kernel;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Every MCP call is reasoned through the cognitive cycle rather than executed beside it
/// (RFC 045 rule 3, step 10b).
/// </summary>
public sealed class KernelDispatcherTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");
    private static readonly Principal Caller = new("local-mcp-client", "paulo");

    private const string EchoSchema =
        """{"type":"object","additionalProperties":false,"required":["message"],"properties":{"message":{"type":"string"}}}""";

    private static FakeCapability Echo() =>
        new(FakeCapability.LowReadOnly("echo.say", EchoSchema),
            input => JsonSerializer.SerializeToElement(
                new Dictionary<string, string>
                {
                    ["said"] = input.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "",
                }));

    /// <summary>A capability that reaches outside Aurora, so deciding about it is not academic.</summary>
    private static FakeCapability Effectful() =>
        new(
            new CapabilityDescriptor(
                "mail.send", "mail.send", "sends a message outside Aurora",
                JsonDocument.Parse(EchoSchema).RootElement.Clone(),
                ["network.egress"], RiskLevel.Medium, true),
            _ => JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["sent"] = "yes" }));

    private static (KernelDispatcher Dispatcher, SqliteCognitiveCycle Cycle, FakeCapability Capability)
        Build(SqliteTestDb db, FakeCapability capability, ReasonerProposal? proposal = null)
    {
        var clock = new TestClock(Now);
        var anchorPath = Path.Combine(Path.GetTempPath(), $"aurora-anchor-{Guid.NewGuid():N}");

        var kernel = new AuroraKernel(
            new FakeReasoner(proposal),
            new FakeRegistry(capability),
            new FakeValidator(true),
            new FakePolicy(true),
            new FakeConsent(true),
            new FakeApprovalStore(),
            new DirectExecutor(),
            new SqliteAuditStore(db.Factory, clock, new byte[32], new AuditAnchorFile(anchorPath)),
            new InMemoryIdempotencyStore(),
            new Adapters.Observability.InMemoryMetrics(clock),
            new FakePassphrase(),
            TestBus.Over(db.Factory, clock));

        var cycle = new SqliteCognitiveCycle(db.Factory, clock);

        var dispatcher = new KernelDispatcher(
            kernel,
            cycle,
            new SqliteEventBus(db.Factory, new SqliteOutbox(new PermissiveEventCatalogue(), clock), clock),
            new SqliteAttentionSystem(db.Factory, new SensitivityAttentionAuthorization(), clock),
            new SqliteWorkingMemory(db.Factory, clock, WorkingMemoryOptions.Default),
            new SqliteMemoryService(db.Factory, new LexicalMemoryRanker(), TestBus.Over(db.Factory, clock), clock),
            new SqliteWorldModel(db.Factory, clock, WorldModelOptions.Default),
            new SqliteDecisionEngine(db.Factory, clock),
            new SqliteObservationService(db.Factory, clock),
            Deliberation(db, cycle, clock),
            AttentionPolicy.Default,
            clock);

        return (dispatcher, cycle, capability);
    }

    /// <summary>A real deliberation service: the dispatcher's explanation has to actually be written.</summary>
    private static SqliteDeliberationService Deliberation(
        SqliteTestDb db, SqliteCognitiveCycle cycles, TestClock clock) =>
        new(db.Factory, cycles,
            new Adapters.Vault.AesGcmSecretProtector(Enumerable.Repeat((byte)7, 32).ToArray()), clock);

    private static JsonElement Message(string text) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["message"] = text });

    // ---- the call goes through the cycle, not beside it ----

    [Fact]
    public async Task AnExecutedCallLeavesACycleWithEveryStageAccountedFor()
    {
        using var db = new SqliteTestDb();
        var (dispatcher, cycle, capability) = Build(db, Echo());

        ExecuteResponse response = await dispatcher.DispatchAsync(
            new ExecuteRequest(ActionId: "echo.say", Input: Message("hello")), Caller, null, Ct);

        Assert.Equal(ExecuteStatus.Completed, response.Status);
        Assert.Equal(1, capability.ExecuteCount);
        Assert.NotNull(response.CycleRef);

        IReadOnlyList<CycleStageRecord> stages = await cycle.StagesAsync(response.CycleRef!, Ct);

        // Rule 1: every stage either ran or carries a reason it did not. No stage is simply absent.
        foreach (var stage in CycleStage.Order)
        {
            CycleStageRecord? record = stages.FirstOrDefault(s => s.Stage == stage);
            Assert.NotNull(record);
            Assert.True(
                record!.Status is StageStatus.Done or StageStatus.Omitted,
                $"{stage} was {record.Status}");
        }

        CognitiveCycle? closed = await cycle.GetAsync(response.CycleRef!, Ct);
        Assert.Equal(CycleStatus.Completed, closed!.Status);
        Assert.True(closed.Executed);
    }

    [Fact]
    public async Task TheEffectIsObservedAndReflectedOnBeforeTheCycleCloses()
    {
        using var db = new SqliteTestDb();
        var (dispatcher, cycle, _) = Build(db, Echo());

        ExecuteResponse response = await dispatcher.DispatchAsync(
            new ExecuteRequest(ActionId: "echo.say", Input: Message("hello")), Caller, null, Ct);

        IReadOnlyList<CycleStageRecord> stages = await cycle.StagesAsync(response.CycleRef!, Ct);

        // RFC 021 rule 5: an execution that is never looked at afterwards is not a finished cycle.
        Assert.Equal(
            StageStatus.Done,
            stages.Single(s => s.Stage == CycleStage.Observation).Status);
        Assert.Equal(
            StageStatus.Done,
            stages.Single(s => s.Stage == CycleStage.Reflection).Status);
    }

    [Fact]
    public async Task TheDecisionPrecedesThePolicyStageWhichPrecedesTheEffect()
    {
        using var db = new SqliteTestDb();
        var (dispatcher, cycle, _) = Build(db, Echo());

        ExecuteResponse response = await dispatcher.DispatchAsync(
            new ExecuteRequest(ActionId: "echo.say", Input: Message("hello")), Caller, null, Ct);

        IReadOnlyList<CycleStageRecord> stages = await cycle.StagesAsync(response.CycleRef!, Ct);
        var order = stages.Select(s => s.Stage).ToList();

        // The property that makes the cycle worth running: permission was established before the
        // effect, not reconstructed around it.
        Assert.True(order.IndexOf(CycleStage.Decision) < order.IndexOf(CycleStage.Policy));
        Assert.True(order.IndexOf(CycleStage.Policy) < order.IndexOf(CycleStage.Executor));
    }

    [Fact]
    public async Task AnExecutedCallLeavesAnExplanationOfWhyItRan()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(Now);
        var cycle = new SqliteCognitiveCycle(db.Factory, clock);
        SqliteDeliberationService deliberation = Deliberation(db, cycle, clock);

        var kernel = new AuroraKernel(
            new FakeReasoner(null), new FakeRegistry(Echo()), new FakeValidator(true),
            new FakePolicy(true), new FakeConsent(true), new FakeApprovalStore(),
            new DirectExecutor(),
            new SqliteAuditStore(
                db.Factory, clock, new byte[32],
                new AuditAnchorFile(Path.Combine(Path.GetTempPath(), $"a-{Guid.NewGuid():N}"))),
            new InMemoryIdempotencyStore(),
            new Adapters.Observability.InMemoryMetrics(clock), new FakePassphrase(),
            TestBus.Over(db.Factory, clock));

        var dispatcher = new KernelDispatcher(
            kernel, cycle, TestBus.Over(db.Factory, clock),
            new SqliteAttentionSystem(db.Factory, new SensitivityAttentionAuthorization(), clock),
            new SqliteWorkingMemory(db.Factory, clock, WorkingMemoryOptions.Default),
            new SqliteMemoryService(db.Factory, new LexicalMemoryRanker(), TestBus.Over(db.Factory, clock), clock),
            new SqliteWorldModel(db.Factory, clock, WorldModelOptions.Default),
            new SqliteDecisionEngine(db.Factory, clock),
            new SqliteObservationService(db.Factory, clock),
            deliberation, AttentionPolicy.Default, clock);

        ExecuteResponse response = await dispatcher.DispatchAsync(
            new ExecuteRequest(ActionId: "echo.say", Input: Message("hello")), Caller, null, Ct);

        Assert.Equal(ExecuteStatus.Completed, response.Status);

        Thought explanation = Assert.Single(
            await deliberation.ThoughtsForCycleAsync(response.CycleRef!, Ct));

        // The cycle already recorded *that* a decision happened. This is what lets Aurora say why
        // afterwards, from its own record rather than from a model asked to reconstruct it.
        Assert.Equal("echo.say", explanation.Intent);
        Assert.Contains("Because:", explanation.UserExplanation, StringComparison.Ordinal);
        Assert.Contains("Sources:", explanation.UserExplanation, StringComparison.Ordinal);
        Assert.NotEmpty(explanation.EvidenceRefs);
    }

    // ---- the decision is real: it has a branch that refuses to act ----

    [Fact]
    public async Task AnInferredActionThatReachesOutsideAuroraIsAskedAboutRatherThanRun()
    {
        using var db = new SqliteTestDb();
        FakeCapability effectful = Effectful();

        // A reasoner that reads an objective as a capability with external effects. Aurora did not
        // hear this instruction; it inferred it, and acting on its own reading is the failure mode.
        var (dispatcher, cycle, _) = Build(
            db, effectful,
            new ReasonerProposal("mail.send", Message("hi"), 0.7, ResolutionVia.Reasoner));

        ExecuteResponse response = await dispatcher.DispatchAsync(
            new ExecuteRequest(Objective: "let them know I am running late"), Caller, null, Ct);

        Assert.Equal(ExecuteStatus.Asked, response.Status);
        Assert.Equal(ErrorCodes.ClarificationRequired, response.Error?.Code);
        Assert.Equal(0, effectful.ExecuteCount);

        // And nothing was executed, so the cycle says so.
        CognitiveCycle? closed = await cycle.GetAsync(response.CycleRef!, Ct);
        Assert.False(closed!.Executed);
        Assert.Equal(CycleStatus.Completed, closed.Status);
    }

    [Fact]
    public async Task AnActionTheCallerNamedIsRunRatherThanAskedAbout()
    {
        using var db = new SqliteTestDb();
        FakeCapability effectful = Effectful();
        var (dispatcher, _, _) = Build(db, effectful);

        // Same capability, same effects — but the caller named it. Asking what was meant when it
        // was already said is not caution, it is a round trip that answers nothing.
        ExecuteResponse response = await dispatcher.DispatchAsync(
            new ExecuteRequest(ActionId: "mail.send", Input: Message("hi")), Caller, null, Ct);

        Assert.Equal(ExecuteStatus.Completed, response.Status);
        Assert.Equal(1, effectful.ExecuteCount);
    }

    // ---- the kernel still has the last word ----

    [Fact]
    public async Task AnActionPolicyRefusesIsNotRunAndItsDecisionIsNotCommitted()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(Now);
        FakeCapability capability = Echo();

        var kernel = new AuroraKernel(
            new FakeReasoner(null), new FakeRegistry(capability), new FakeValidator(true),
            new FakePolicy(allow: false), new FakeConsent(true), new FakeApprovalStore(),
            new DirectExecutor(),
            new SqliteAuditStore(
                db.Factory, clock, new byte[32],
                new AuditAnchorFile(Path.Combine(Path.GetTempPath(), $"a-{Guid.NewGuid():N}"))),
            new InMemoryIdempotencyStore(),
            new Adapters.Observability.InMemoryMetrics(clock), new FakePassphrase(),
            TestBus.Over(db.Factory, clock));

        var cycle = new SqliteCognitiveCycle(db.Factory, clock);
        var decisions = new SqliteDecisionEngine(db.Factory, clock);

        var dispatcher = new KernelDispatcher(
            kernel, cycle, new SqliteEventBus(db.Factory, new SqliteOutbox(new PermissiveEventCatalogue(), clock), clock),
            new SqliteAttentionSystem(db.Factory, new SensitivityAttentionAuthorization(), clock),
            new SqliteWorkingMemory(db.Factory, clock, WorkingMemoryOptions.Default),
            new SqliteMemoryService(db.Factory, new LexicalMemoryRanker(), TestBus.Over(db.Factory, clock), clock),
            new SqliteWorldModel(db.Factory, clock, WorldModelOptions.Default),
            decisions, new SqliteObservationService(db.Factory, clock),
            Deliberation(db, cycle, clock),
            AttentionPolicy.Default, clock);

        ExecuteResponse response = await dispatcher.DispatchAsync(
            new ExecuteRequest(ActionId: "echo.say", Input: Message("hello")), Caller, null, Ct);

        Assert.Equal(ExecuteStatus.Denied, response.Status);
        Assert.Equal(ErrorCodes.PolicyDenied, response.Error?.Code);
        Assert.Equal(0, capability.ExecuteCount);

        // The cycle chose to act and was overruled. Recording that decision as COMMITTED would say
        // Aurora made a call it never got to make.
        CycleStageRecord decisionStage = (await cycle.StagesAsync(response.CycleRef!, Ct))
            .Single(s => s.Stage == CycleStage.Decision);
        Decision? decision = await decisions.GetAsync(decisionStage.DecisionRef!, Ct);

        Assert.Equal(DecisionState.Superseded, decision!.Status);
    }

    [Fact]
    public async Task AMalformedCallIsRefusedWithoutOpeningACycle()
    {
        using var db = new SqliteTestDb();
        var (dispatcher, cycle, _) = Build(db, Echo());

        ExecuteResponse response = await dispatcher.DispatchAsync(
            new ExecuteRequest(Objective: "hi", ActionId: "echo.say"), Caller, null, Ct);

        // RFC 021: invalid ingress is refused before any persistent cognitive mutation. A malformed
        // call does not earn a place in the record.
        Assert.Equal(ExecuteStatus.Invalid, response.Status);
        Assert.Equal(ErrorCodes.BothModes, response.Error?.Code);
        Assert.Null(response.CycleRef);
    }
}
