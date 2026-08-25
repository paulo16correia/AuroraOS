using System.Globalization;
using Aurora.Adapters.Cognition;
using Aurora.Adapters.Deliberation;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Vault;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Internal deliberation and explainable synthesis (RFC 025).
/// </summary>
/// <remarks>
/// The separation is the subject: how Aurora worked is protected and short-lived, and what it can
/// say about it is a Thought. Most of these tests are about the wall between the two.
/// </remarks>
public sealed class DeliberationTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record World(
        SqliteDeliberationService Deliberation, SqliteCognitiveCycle Cycles, TestClock Clock);

    private static World Build(SqliteTestDb db, string now = "2026-01-15T09:00:00+00:00")
    {
        var clock = new TestClock(At(now));
        var cycles = new SqliteCognitiveCycle(db.Factory, clock);

        return new World(
            new SqliteDeliberationService(
                db.Factory, cycles,
                new AesGcmSecretProtector(Enumerable.Repeat((byte)7, 32).ToArray()), clock),
            cycles, clock);
    }

    private static Task<CognitiveCycle> CycleAsync(World world) =>
        world.Cycles.RunAsync(new CycleIngress("work", Guid.NewGuid().ToString("N"), null), Ct);

    private static DateTimeOffset Soon(World world) => world.Clock.UtcNow.AddMinutes(30);

    // ---- rule 1: no ownerless global mental process ----

    [Fact]
    public async Task ADeliberationCannotExistWithoutACycle()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        await Assert.ThrowsAsync<DeliberationException>(() =>
            world.Deliberation.StartAsync("no-such-cycle", "what now?", Soon(world), Ct));
    }

    [Fact]
    public async Task ADeliberationCannotExistWithoutADeadline()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        CognitiveCycle cycle = await CycleAsync(world);

        // The other half of rule 1: something that never has to finish is not bounded by a cycle
        // in any sense that matters.
        await Assert.ThrowsAsync<DeliberationException>(() =>
            world.Deliberation.StartAsync(cycle.Id, "what now?", world.Clock.UtcNow, Ct));
    }

    [Fact]
    public async Task AClosedCycleDeliberatesNoFurther()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        CognitiveCycle cycle = await CycleAsync(world);

        await world.Cycles.CompleteAsync(cycle.Id, carriesPersistentStateOrExecution: false, "done", Ct);

        await Assert.ThrowsAsync<DeliberationException>(() =>
            world.Deliberation.StartAsync(cycle.Id, "what now?", Soon(world), Ct));
    }

    // ---- phases move forward ----

    [Fact]
    public async Task PhasesRunInOrderAndDoNotGoBack()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        CognitiveCycle cycle = await CycleAsync(world);

        DeliberationState state = await world.Deliberation
            .StartAsync(cycle.Id, "which option?", Soon(world), Ct);

        Assert.Equal(DeliberationPhase.Orient, state.Phase);

        state = await world.Deliberation.AdvanceAsync(
            state.Id, DeliberationPhase.Compare, new DeliberationStep(), Ct);

        Assert.Equal(DeliberationPhase.Compare, state.Phase);

        // Deliberation that can revisit any phase at will has no shape, and a record of it explains
        // nothing about the order things were considered in.
        await Assert.ThrowsAsync<DeliberationException>(() =>
            world.Deliberation.AdvanceAsync(
                state.Id, DeliberationPhase.Orient, new DeliberationStep(), Ct));
    }

    // ---- rule 2: a claim without evidence stays a hypothesis ----

    [Fact]
    public async Task AClaimWithNoEvidenceIsAHypothesisAndSaysSoInTheExplanation()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        CognitiveCycle cycle = await CycleAsync(world);

        DeliberationState state = await world.Deliberation
            .StartAsync(cycle.Id, "does the owner prefer tea?", Soon(world), Ct);

        state = await world.Deliberation.AdvanceAsync(
            state.Id, DeliberationPhase.Retrieve,
            new DeliberationStep(
                Assertions:
                [
                    new Assertion("the owner drinks tea in the morning", ["memory/1"], 0.8),
                    new Assertion("the owner dislikes coffee", [], 0.4),
                ],
                ResolvedQuestions: ["does the owner prefer tea?"]),
            Ct);

        Assert.True(state.Assertions[1].IsHypothesis);
        Assert.False(state.Assertions[0].IsHypothesis);

        Thought thought = await world.Deliberation.SummariseAsync(
            state.Id, new ThoughtRequest("answer", "say tea", ["say tea", "ask"]), Ct);

        // The unsupported claim does not silently become part of the answer: it is carried into the
        // uncertainty, where a reader can see it was never established.
        Assert.Contains(thought.Uncertainty, u => u.Contains("dislikes coffee", StringComparison.Ordinal));
        Assert.Contains("memory/1", thought.EvidenceRefs);
    }

    // ---- rule 3: never offer "I am thinking" as evidence of work ----

    [Fact]
    public async Task TheExplanationCarriesReasonSourcesAndNextEffectAndNothingAboutThinking()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        CognitiveCycle cycle = await CycleAsync(world);

        DeliberationState state = await world.Deliberation
            .StartAsync(cycle.Id, "which option?", Soon(world), Ct);

        state = await world.Deliberation.AdvanceAsync(
            state.Id, DeliberationPhase.Decide,
            new DeliberationStep(
                Assertions: [new Assertion("option A is cheaper", ["quote/1"], 0.9)],
                ResolvedQuestions: ["which option?"]),
            Ct);

        Thought thought = await world.Deliberation.SummariseAsync(
            state.Id, new ThoughtRequest("choose", "take option A", ["A", "B"]), Ct);

        Assert.Contains("Because:", thought.UserExplanation, StringComparison.Ordinal);
        Assert.Contains("Sources:", thought.UserExplanation, StringComparison.Ordinal);
        Assert.Contains("Next:", thought.UserExplanation, StringComparison.Ordinal);

        // The shape cannot express a claim about ongoing internal activity, because there is no
        // clause for one. A free-form field is exactly where that sentence gets in.
        foreach (var claim in new[] { "thinking", "working on", "processing", "considering it" })
        {
            Assert.DoesNotContain(claim, thought.UserExplanation, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- rule 4: the trace is protected, brief, and unreadable through any interface ----

    [Fact]
    public void TheTraceCanBeAskedAboutButNeverRead()
    {
        // The strongest form of "the trace is not exported": no method can return one. Held by
        // reflection rather than by discipline, so a later change cannot quietly add a getter.
        //
        // Asking *whether* a trace is still there is fine and necessary — a decision whose trace is
        // gone stands only if its sources are recoverable without it, and a caller has to be able
        // to find out which case it is in. So the rule is not "nothing mentions the trace", it is
        // "anything that does answers yes or no".
        var aboutTraces = typeof(IDeliberationService).GetMethods()
            .Where(m => m.Name.Contains("Trace", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(aboutTraces);

        Assert.All(aboutTraces, method =>
        {
            Type returned = method.ReturnType.IsGenericType
                ? method.ReturnType.GetGenericArguments()[0]
                : method.ReturnType;

            Assert.Equal(typeof(bool), returned);
        });

        // And nothing else on the surface hands back anything that could carry the material.
        Assert.DoesNotContain(
            typeof(IDeliberationService).GetMethods(),
            m => m.ReturnType.IsGenericType
              && m.ReturnType.GetGenericArguments()[0] == typeof(string));
    }

    [Fact]
    public async Task ATraceIsEncryptedAtRestAndTheWorkingNotesNeverAppearInTheDatabase()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        CognitiveCycle cycle = await CycleAsync(world);

        const string notes = "the owner mentioned their bank in passing, which is not relevant here";

        DeliberationState state = await world.Deliberation
            .StartAsync(cycle.Id, "which option?", Soon(world), Ct);

        state = await world.Deliberation.AdvanceAsync(
            state.Id, DeliberationPhase.Retrieve, new DeliberationStep(Trace: notes), Ct);

        Assert.NotNull(state.TraceRef);
        Assert.True(await world.Deliberation.TraceAvailableAsync(state.Id, Ct));

        // Read the raw column: the material must not be sitting there in the clear.
        await using SqliteConnection connection = await db.Factory.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ciphertext FROM deliberation_trace WHERE trace_ref = @r;";
        command.Parameters.AddWithValue("@r", state.TraceRef!);

        var ciphertext = (byte[])(await command.ExecuteScalarAsync(Ct))!;

        Assert.DoesNotContain("bank", System.Text.Encoding.UTF8.GetString(ciphertext), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATraceIsDiscardedAfterItsRetentionAndTheThoughtSurvives()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        CognitiveCycle cycle = await CycleAsync(world);

        DeliberationState state = await world.Deliberation
            .StartAsync(cycle.Id, "which option?", Soon(world), Ct);

        state = await world.Deliberation.AdvanceAsync(
            state.Id, DeliberationPhase.Decide,
            new DeliberationStep(
                Assertions: [new Assertion("A is cheaper", ["quote/1"], 0.9)],
                ResolvedQuestions: ["which option?"],
                Trace: "working notes"),
            Ct);

        Thought thought = await world.Deliberation.SummariseAsync(
            state.Id, new ThoughtRequest("choose", "take A", ["A", "B"]), Ct);

        world.Clock.UtcNow = At("2026-02-15T09:00:00+00:00");
        await world.Deliberation.ExpireDueAsync(Ct);

        Assert.False(await world.Deliberation.TraceAvailableAsync(state.Id, Ct));

        // What survives is the explanation, which is the part that was ever meant to.
        Thought? kept = await world.Deliberation.ThoughtAsync(thought.Id, Ct);
        Assert.NotNull(kept);
        Assert.Contains("quote/1", kept!.EvidenceRefs);
    }

    // ---- limit case: an inconclusive deliberation leaves questions ----

    [Fact]
    public async Task AnInconclusiveDeliberationMustLeaveTheQuestionsItCouldNotAnswer()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        CognitiveCycle cycle = await CycleAsync(world);

        DeliberationState state = await world.Deliberation
            .StartAsync(cycle.Id, "which supplier?", Soon(world), Ct);

        DeliberationState closed = await world.Deliberation
            .CloseAsync(state.Id, DeliberationDisposition.Inconclusive, Ct);

        Assert.Equal(DeliberationStatus.Closed, closed.Status);
        Assert.Contains("which supplier?", closed.UnresolvedQuestions);

        // And with nothing left open, INCONCLUSIVE is not the honest word for it.
        DeliberationState answered = await world.Deliberation
            .StartAsync((await CycleAsync(world)).Id, "which one?", Soon(world), Ct);

        answered = await world.Deliberation.AdvanceAsync(
            answered.Id, DeliberationPhase.Decide,
            new DeliberationStep(ResolvedQuestions: ["which one?"]), Ct);

        await Assert.ThrowsAsync<DeliberationException>(() =>
            world.Deliberation.CloseAsync(answered.Id, DeliberationDisposition.Inconclusive, Ct));
    }

    [Fact]
    public async Task ADeliberationWithOpenQuestionsCannotBeCalledConcluded()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        CognitiveCycle cycle = await CycleAsync(world);

        DeliberationState state = await world.Deliberation
            .StartAsync(cycle.Id, "which supplier?", Soon(world), Ct);

        // Reporting a dead end as a conclusion is the failure this prevents.
        await Assert.ThrowsAsync<DeliberationException>(() =>
            world.Deliberation.CloseAsync(state.Id, DeliberationDisposition.Concluded, Ct));
    }

    [Fact]
    public async Task ADeliberationPastItsDeadlineIsClosedRatherThanLeftRunning()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        CognitiveCycle cycle = await CycleAsync(world);

        DeliberationState state = await world.Deliberation
            .StartAsync(cycle.Id, "which option?", Soon(world), Ct);

        world.Clock.UtcNow = At("2026-01-15T10:00:00+00:00");
        await world.Deliberation.ExpireDueAsync(Ct);

        DeliberationState expired = (await world.Deliberation.GetAsync(state.Id, Ct))!;

        Assert.Equal(DeliberationStatus.Closed, expired.Status);
        Assert.Equal(DeliberationDisposition.Expired, expired.NextStep);

        await Assert.ThrowsAsync<DeliberationException>(() =>
            world.Deliberation.AdvanceAsync(state.Id, DeliberationPhase.Verify, new DeliberationStep(), Ct));
    }

    // ---- limit case: asking how it decided returns an explanation, not the private trace ----

    [Fact]
    public async Task AskingHowItDecidedReturnsTheExplanationAndItsSourcesAndNotTheWorkingNotes()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);
        CognitiveCycle cycle = await CycleAsync(world);

        const string notes = "half-formed comparison nobody should be handed as reasoning";

        DeliberationState state = await world.Deliberation
            .StartAsync(cycle.Id, "which option?", Soon(world), Ct);

        state = await world.Deliberation.AdvanceAsync(
            state.Id, DeliberationPhase.Decide,
            new DeliberationStep(
                Assertions: [new Assertion("A is cheaper", ["quote/1"], 0.9)],
                ResolvedQuestions: ["which option?"],
                Trace: notes),
            Ct);

        Thought thought = await world.Deliberation.SummariseAsync(
            state.Id, new ThoughtRequest("choose", "take A", ["A", "B"]), Ct);

        // The explanation is built from the state, and the state and the trace are read by
        // different code paths from different tables: summarising cannot reach the material.
        Assert.DoesNotContain("half-formed", thought.UserExplanation, StringComparison.Ordinal);
        Assert.DoesNotContain(notes, thought.UserExplanation, StringComparison.Ordinal);
        Assert.Contains("quote/1", thought.EvidenceRefs);
    }
}
