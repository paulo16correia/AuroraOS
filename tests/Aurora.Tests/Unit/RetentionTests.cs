using System.Globalization;
using System.Text.Json;
using Aurora.Adapters.Cognition;
using Aurora.Adapters.Curiosity;
using Aurora.Adapters.Events;
using Aurora.Adapters.Memories;
using Aurora.Adapters.Needs;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Planning;
using Aurora.Adapters.Resources;
using Aurora.Adapters.Retention;
using Aurora.Adapters.Scheduling;
using Aurora.Adapters.Signals;
using Aurora.Adapters.Situation;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Kernel;
using Aurora.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Retention (docs/adr/0036): forgetting the by-products of working, and nothing else.
/// </summary>
public sealed class RetentionTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Lisbon = "Europe/Lisbon";

    private static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    [Fact]
    public async Task AClosedCycleAgesOutAndTakesItsStageRecordsWithIt()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-01T00:00:00+00:00"));
        var cycles = new SqliteCognitiveCycle(db.Factory, clock);

        CognitiveCycle cycle = await cycles.RunAsync(new CycleIngress("w", "c-1", null), Ct);
        await cycles.AdvanceAsync(cycle.Id, CycleStage.Perception, [], ["x"], null, Ct);
        await cycles.CompleteAsync(cycle.Id, carriesPersistentStateOrExecution: false, "done", Ct);

        clock.UtcNow = At("2026-06-01T00:00:00+00:00");

        RetentionReport removed = await new SqliteRetentionService(db.Factory, clock)
            .ApplyAsync(RetentionPolicy.Default, Ct);

        Assert.Equal(1, removed.Cycles);
        Assert.True(removed.CycleStages >= 1);
        Assert.Null(await cycles.GetAsync(cycle.Id, Ct));
    }

    [Fact]
    public async Task AnExpiredCycleTakesItsScratchWithItAndLeavesWhatWasDone()
    {
        // A real call through the real dispatcher, so every table a cycle writes to is written.
        await using var server = new AuroraAppFactory();
        string cycleId;

        using (IServiceScope scope = server.Services.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<KernelDispatcher>();

            ExecuteResponse done = await dispatcher.DispatchAsync(
                new ExecuteRequest(
                    ActionId: "echo.say",
                    Input: JsonSerializer.SerializeToElement(new { message = "kept?" })),
                new Principal("local-mcp-client", "owner"), null, Ct);

            Assert.Equal(ExecuteStatus.Completed, done.Status);
            cycleId = done.CycleRef!;
        }

        var factory = new SqliteConnectionFactory(server.DbPath);

        async Task<long> CountAsync(string sql)
        {
            await using var connection = await factory.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@c", cycleId);
            return (long)(await command.ExecuteScalarAsync(Ct))!;
        }

        const string Deliberations = "SELECT COUNT(*) FROM deliberation WHERE cycle_id = @c;";
        const string Thoughts = "SELECT COUNT(*) FROM thought WHERE cycle_id = @c;";
        const string Frames = "SELECT COUNT(*) FROM working_memory WHERE cycle_id = @c;";
        const string Attention = "SELECT COUNT(*) FROM attention_set WHERE cycle_id = @c;";
        const string Decisions = "SELECT COUNT(*) FROM decision WHERE cycle_id = @c;";

        // Everything this test is about exists before the pass, or the pass proves nothing.
        foreach (var sql in new[] { Deliberations, Thoughts, Frames, Attention, Decisions })
        {
            Assert.True(await CountAsync(sql) > 0, sql);
        }

        var later = new TestClock(DateTimeOffset.UtcNow.AddDays(91));
        RetentionReport removed = await new SqliteRetentionService(factory, later)
            .ApplyAsync(RetentionPolicy.Default, Ct);

        Assert.True(removed.CycleScratch > 0);

        // How it thought is gone with the cycle; nothing could read it any more.
        foreach (var sql in new[] { Deliberations, Thoughts, Frames, Attention })
        {
            Assert.Equal(0, await CountAsync(sql));
        }

        // What it decided stays, as does the audit record of the effect.
        Assert.True(await CountAsync(Decisions) > 0);
        Assert.True(await CountAsync(
            "SELECT COUNT(*) FROM audit_record WHERE action_id = 'echo.say' AND @c IS NOT NULL;") > 0);
    }

    [Fact]
    public async Task ACycleStillRunningNeverAgesOutHoweverOldItIs()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-01T00:00:00+00:00"));
        var cycles = new SqliteCognitiveCycle(db.Factory, clock);

        CognitiveCycle open = await cycles.RunAsync(new CycleIngress("w", "c-2", null), Ct);

        clock.UtcNow = At("2027-01-01T00:00:00+00:00");
        await new SqliteRetentionService(db.Factory, clock).ApplyAsync(RetentionPolicy.Default, Ct);

        // An old unfinished cycle is the most interesting record in the table, not the least.
        Assert.NotNull(await cycles.GetAsync(open.Id, Ct));
    }

    [Fact]
    public async Task RetentionNeverTouchesTheAuditChain()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-01T00:00:00+00:00"));
        var anchor = TestTemp.Path("anchor");
        var audit = new SqliteAuditStore(db.Factory, clock, new byte[32], new AuditAnchorFile(anchor));

        await audit.AppendAsync(
            new AuditEntry("c1", "u1", "echo.say", "h", "completed",
                Risk: "Low", Via: ResolutionVia.Explicit, Decision: "auto_low", PolicyIds: "p"),
            Ct);

        clock.UtcNow = At("2030-01-01T00:00:00+00:00");
        await new SqliteRetentionService(db.Factory, clock).ApplyAsync(RetentionPolicy.Default, Ct);

        // A system that tidies away its own history on a schedule is one whose history cannot be
        // relied on — and the chain would stop verifying if a single record vanished.
        Assert.Single(await audit.QueryAsync(0, 100, Ct));
        Assert.True((await audit.VerifyChainAsync(Ct)).Ok);
    }

    [Fact]
    public async Task RetentionNeverTouchesMemoriesGoalsOrMissions()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-01T00:00:00+00:00"));
        var planner = new SqlitePlanner(db.Factory, clock, TestBus.Over(db.Factory, clock));
        var memories = new SqliteMemoryService(
            db.Factory, new LexicalMemoryRanker(), TestBus.Over(db.Factory, clock), clock);

        MemoryRecord memory = await memories.RecordAsync(
            new MemoryCandidate(
                MemoryKind.Semantic, "person/owner", "prefers", """{"v":"tea"}""",
                "the owner prefers tea", 0.9, Sensitivity.Private),
            new MemoryProvenance(
                ["c/1"], ["t/1"], MemoryOrigin.User, MemoryAccessPolicy.Owner,
                [new MemoryAnchor(MemoryAnchorKind.Conversation, "c/1", "the owner said so")]),
            Ct);

        Goal goal = await planner.DraftAsync(
            new GoalRequest("something", "an outcome", "paulo", [], []), Ct);

        clock.UtcNow = At("2030-01-01T00:00:00+00:00");
        await new SqliteRetentionService(db.Factory, clock).ApplyAsync(RetentionPolicy.Default, Ct);

        Assert.NotNull(await memories.GetAsync(memory.Id, Ct));
        Assert.NotNull(await planner.GetGoalAsync(goal.Id, Ct));
    }

    [Fact]
    public async Task SettledRunsResolvedSignalsAndDeadQuestionsAgeOut()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T08:00:00+00:00"));
        var cycles = new SqliteCognitiveCycle(db.Factory, clock);
        var bus = new SqliteEventBus(db.Factory, new SqliteOutbox(new PermissiveEventCatalogue(), clock), clock);
        var scheduler = new SqliteScheduler(db.Factory, bus, cycles, clock);
        var signals = new SqliteSignalService(db.Factory, cycles, clock);
        var planner = new SqlitePlanner(db.Factory, clock, TestBus.Over(db.Factory, clock));
        var needs = new SqliteNeedsService(db.Factory, planner, clock);
        var resources = new SystemResourceModel(new FakeResourceProbe(), clock);
        var situation = new SituationService(signals, needs, resources, QuietHours.Default, clock);
        var curiosity = new SqliteCuriosityEngine(db.Factory, planner, resources, situation, needs, clock);

        Schedule schedule = await scheduler.CreateAsync(
            new ScheduleRequest("daily", "paulo", ScheduleTrigger.Cron, Lisbon, "0 9 * * *",
                ScheduleTarget.CycleTemplate),
            null, Ct);

        clock.UtcNow = At("2026-01-15T09:00:00+00:00");
        ScheduleRun due = (await scheduler.TickAsync(clock.UtcNow, Ct)).Single();
        await scheduler.StartAsync(due.Id, "cycle-x", Ct);
        await scheduler.FinishAsync(due.Id, ScheduleRunStatus.Succeeded, null, Ct);

        DomainEvent fact = await bus.PublishAsync(
            new OutboxWrite("Observed", 1, "health", "c-1", Sensitivity.Private, PayloadJson: "{}"), Ct);

        Signal signal = await signals.EmitAsync(
            fact.EventId,
            new SignalClassification(
                SignalKind.Health, SignalSeverity.Low, 0.2, 0.2, 0.9, ["h"], TimeSpan.FromMinutes(5)),
            SignalPolicy.Default, Ct);

        await signals.AcknowledgeAsync(signal.Id, "handled", Ct);

        CuriosityProposal question = (await curiosity.DetectAsync(
            new CuriositySnapshot(
            [
                new KnowledgeGap("topic/x", "what?", 5, 0.2, ["c/1"], "the whole internet"),
            ]),
            CuriosityPolicy.Default, Ct)).Single();

        Assert.Equal(CuriosityStatus.Rejected, question.Status);

        clock.UtcNow = At("2026-03-15T09:00:00+00:00");
        RetentionReport removed = await new SqliteRetentionService(db.Factory, clock)
            .ApplyAsync(RetentionPolicy.Default, Ct);

        Assert.Equal(1, removed.ScheduleRuns);
        Assert.Equal(1, removed.Signals);
        Assert.Equal(1, removed.CuriosityProposals);

        Assert.Empty(await scheduler.RunsAsync(schedule.Id, Ct));
        Assert.Null(await signals.GetAsync(signal.Id, Ct));
        Assert.Null(await curiosity.GetAsync(question.Id, Ct));
    }

    [Fact]
    public async Task ADeploymentThatWouldRatherGrowThanForgetKeepsEverything()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-01T00:00:00+00:00"));
        var cycles = new SqliteCognitiveCycle(db.Factory, clock);

        CognitiveCycle cycle = await cycles.RunAsync(new CycleIngress("w", "c-3", null), Ct);
        await cycles.CompleteAsync(cycle.Id, carriesPersistentStateOrExecution: false, "done", Ct);

        clock.UtcNow = At("2040-01-01T00:00:00+00:00");

        // TimeSpan.MaxValue means keep. Subtracting it from now would overflow, so it is checked
        // rather than turned into a cutoff — a crash would be a poor reward for caution.
        RetentionReport removed = await new SqliteRetentionService(db.Factory, clock)
            .ApplyAsync(RetentionPolicy.KeepEverything, Ct);

        Assert.Equal(0, removed.Total);
        Assert.NotNull(await cycles.GetAsync(cycle.Id, Ct));
    }
}
