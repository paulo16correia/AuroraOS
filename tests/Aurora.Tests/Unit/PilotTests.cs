using Aurora.Adapters.Cognition;
using Aurora.Adapters.WorkItems;
using Aurora.Adapters.Constitution;
using Aurora.Adapters.Events;
using Aurora.Adapters.Memories;
using Aurora.Adapters.Observations;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Pilot;
using Aurora.Adapters.World;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// The first vertical slice of the frozen implementation order, end to end.
/// </summary>
/// <remarks>
/// RFC 100: "A local conversation creates an Event, opens loop, reclaims empty/allowed memory,
/// produces Decision(RESPOND), stores audit, creates Observation from the response, and survives
/// restart. Do not use external tools."
/// </remarks>
public sealed class PilotTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");
    private static readonly Principal Caller = new("local-mcp-client", "paulo");

    /// <summary>Builds a full set of services over one database, as a process would.</summary>
    private static (LocalConversationPilot Pilot, SqliteMemoryService Memories,
        SqliteAuditStore Audit, SqliteEventBus Bus, SqliteCognitiveCycle Cycle) Build(SqliteTestDb db)
    {
        var clock = new TestClock(Now);
        var anchorPath = Path.Combine(Path.GetTempPath(), $"aurora-anchor-{Guid.NewGuid():N}");

        var audit = new SqliteAuditStore(db.Factory, clock, new byte[32], new AuditAnchorFile(anchorPath));
        var bus = new SqliteEventBus(db.Factory, new SqliteOutbox(new PermissiveEventCatalogue(), clock), clock);
        var cycle = new SqliteCognitiveCycle(db.Factory, clock);
        var memories = new SqliteMemoryService(db.Factory, new LexicalMemoryRanker(), TestBus.Over(db.Factory, clock), clock);

        var pilot = new LocalConversationPilot(
            cycle,
            new SqliteWorkItemService(db.Factory, clock),
            bus,
            new SqliteAttentionSystem(db.Factory, new SensitivityAttentionAuthorization(), clock),
            new SqliteWorkingMemory(db.Factory, clock, WorkingMemoryOptions.Default),
            memories,
            new SqliteWorldModel(db.Factory, clock, WorldModelOptions.Default),
            new SqliteDecisionEngine(db.Factory, new ArticleConstitution(), clock),
            new SqliteObservationService(db.Factory, new RecordingIncidentService(), clock),
            audit,
            AttentionPolicy.Default,
            clock);

        return (pilot, memories, audit, bus, cycle);
    }

    private static PilotRequest Ask(string utterance = "what do I usually drink") =>
        new("c-1", utterance, Caller);

    private static async Task RememberAsync(SqliteMemoryService memories, string summary)
    {
        await memories.RecordAsync(
            new MemoryCandidate(
                MemoryKind.Semantic, "person/paulo", "prefers", """{"drink":"tea"}""",
                summary, 0.9, Sensitivity.Private),
            new MemoryProvenance(
                ["conversation/earlier"], ["turn/1"], MemoryOrigin.User, "policy/owner",
                [new MemoryAnchor(MemoryAnchorKind.Conversation, "conversation/earlier", "he said so")]),
            Ct);
    }

    // ---- the slice ----

    [Fact]
    public async Task ALocalTurnRunsTheWholeGovernedPathAndDecidesToRespond()
    {
        using var db = new SqliteTestDb();
        var (pilot, memories, _, _, _) = Build(db);
        await RememberAsync(memories, "Paulo usually drinks tea");

        PilotOutcome outcome = await pilot.RespondAsync(Ask(), Ct);

        Assert.False(string.IsNullOrWhiteSpace(outcome.CycleId));
        Assert.Contains(DecisionMode.Respond, outcome.ResponseSummary, StringComparison.Ordinal);
        Assert.Contains("1 recalled memory", outcome.ResponseSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoStageIsSkipped_EachOmissionCarriesItsReason()
    {
        using var db = new SqliteTestDb();
        var (pilot, memories, _, _, cycle) = Build(db);
        await RememberAsync(memories, "Paulo usually drinks tea");

        PilotOutcome outcome = await pilot.RespondAsync(Ask(), Ct);
        IReadOnlyList<CycleStageRecord> stages = await cycle.StagesAsync(outcome.CycleId, Ct);

        // Every stage of RFC 021 is accounted for — run or omitted, never absent.
        Assert.Equal(CycleStage.Order.Count, stages.Count);
        Assert.All(
            stages.Where(s => s.Status == StageStatus.Omitted),
            s => Assert.False(string.IsNullOrWhiteSpace(s.Note)));
    }

    [Fact]
    public async Task NoExternalToolIsUsed()
    {
        using var db = new SqliteTestDb();
        var (pilot, memories, _, _, _) = Build(db);
        await RememberAsync(memories, "Paulo usually drinks tea");

        PilotOutcome outcome = await pilot.RespondAsync(Ask(), Ct);

        // The implementation order is explicit that this slice predates any connector.
        Assert.Contains(CycleStage.Capabilities, outcome.StagesOmitted);
        Assert.DoesNotContain(CycleStage.Capabilities, outcome.StagesRun);
    }

    [Fact]
    public async Task TheTurnIsPublishedAsAnEvent()
    {
        using var db = new SqliteTestDb();
        var (pilot, memories, _, bus, _) = Build(db);
        await RememberAsync(memories, "Paulo usually drinks tea");
        await bus.SubscribeAsync(new Subscription(
            "sub-1", "indexer", ["ConversationTurnReceived"], null, DeliveryMode.AtLeastOnce,
            0, SubscriptionStatus.Active, 3, 1), Ct);

        await pilot.RespondAsync(Ask(), Ct);

        var consumer = new CountingConsumer("indexer");
        Assert.Equal(1, await bus.PumpAsync(consumer, Ct));
        Assert.Equal(Sensitivity.Private, consumer.Last!.SensitivityClass);
        Assert.Equal("pilot", consumer.Last.Producer);
    }

    [Fact]
    public async Task TheResponseIsAnObservedAction()
    {
        using var db = new SqliteTestDb();
        var (pilot, memories, _, _, _) = Build(db);
        await RememberAsync(memories, "Paulo usually drinks tea");
        var observations = new SqliteObservationService(db.Factory, new RecordingIncidentService(), new TestClock(Now));

        PilotOutcome outcome = await pilot.RespondAsync(Ask(), Ct);

        AuroraAction action = (await observations.GetActionAsync(outcome.ActionId, Ct))!;
        Assert.Equal(ActionState.Observed, action.State);

        Observation observation = Assert.Single(await observations.ObservationsAsync(action.Id, Ct));
        Assert.Equal(ObservationState.Validated, observation.State);
        Assert.Equal(ObservationOutcome.Success, observation.Outcome);

        // Nothing is left waiting to be reconciled.
        Assert.Empty(await observations.UnobservedAsync(Ct));
    }

    [Fact]
    public async Task TheTurnIsAudited()
    {
        using var db = new SqliteTestDb();
        var (pilot, memories, audit, _, _) = Build(db);
        await RememberAsync(memories, "Paulo usually drinks tea");

        PilotOutcome outcome = await pilot.RespondAsync(Ask(), Ct);

        Assert.Single(outcome.AuditRefs);
        Assert.True((await audit.VerifyChainAsync(Ct)).Ok);
    }

    [Fact]
    public async Task TheContextIsTemporary()
    {
        using var db = new SqliteTestDb();
        var (pilot, memories, _, _, _) = Build(db);
        await RememberAsync(memories, "Paulo usually drinks tea");
        var working = new SqliteWorkingMemory(db.Factory, new TestClock(Now), WorkingMemoryOptions.Default);

        PilotOutcome outcome = await pilot.RespondAsync(Ask(), Ct);

        // A turn does not leave a growing transcript behind it.
        using var connection = db.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM working_memory;";
        Assert.Equal(WorkingMemoryStatus.Discarded, (string)command.ExecuteScalar()!);
    }

    // ---- survives restart ----

    [Fact]
    public async Task TheTurnSurvivesARestart()
    {
        using var db = new SqliteTestDb();
        string cycleId;

        // One "process".
        {
            var (pilot, memories, _, _, _) = Build(db);
            await RememberAsync(memories, "Paulo usually drinks tea");
            cycleId = (await pilot.RespondAsync(Ask(), Ct)).CycleId;
        }

        // Another, over the same database and sharing nothing in memory.
        var (restarted, _, _, _, cycle) = Build(db);

        PilotOutcome? recalled = await restarted.RecallAsync(cycleId, Ct);

        Assert.NotNull(recalled);
        Assert.Equal(CycleStatus.Completed, recalled!.ResponseSummary);
        Assert.Equal(CycleStage.Order.Count, recalled.StagesRun.Count + recalled.StagesOmitted.Count);
        Assert.False(string.IsNullOrWhiteSpace(recalled.DecisionId));
        Assert.Equal(CycleStatus.Completed, (await cycle.GetAsync(cycleId, Ct))!.Status);
    }

    // ---- ignorance is not invented ----

    [Fact]
    public async Task WithNothingRecalledTheSummarySaysSo()
    {
        using var db = new SqliteTestDb();
        var (pilot, _, _, _, _) = Build(db);

        PilotOutcome outcome = await pilot.RespondAsync(Ask("something never discussed"), Ct);

        // No memory is fabricated to fill the gap.
        Assert.Contains("nothing recorded", outcome.ResponseSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyUtteranceIsRejectedBeforeAnythingIsWritten()
    {
        using var db = new SqliteTestDb();
        var (pilot, _, _, _, _) = Build(db);

        await Assert.ThrowsAsync<CognitiveCycleException>(() => pilot.RespondAsync(Ask("   "), Ct));

        // Perception rejects it without creating a persistent cognitive mutation.
        using var connection = db.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM cognitive_cycle;";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }

    private sealed class CountingConsumer(string name) : IEventConsumer
    {
        public string Name { get; } = name;

        public DomainEvent? Last { get; private set; }

        public Task<ConsumeResult> ConsumeAsync(DomainEvent domainEvent, CancellationToken ct)
        {
            Last = domainEvent;
            return Task.FromResult(new ConsumeResult(ConsumeOutcome.Acked));
        }
    }
}
