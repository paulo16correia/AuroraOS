using Aurora.Adapters.Cognition;
using Aurora.Adapters.Constitution;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>Conformance tests for RFC 022.</summary>
public sealed class DecisionEngineTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");
    private const string CycleId = "cycle-1";

    private static SqliteDecisionEngine Engine(SqliteTestDb db, DateTimeOffset? now = null) =>
        new(db.Factory, new ArticleConstitution(), new TestClock(now ?? Now));

    private static OptionEvaluation Ok(
        double cost = 1, bool permitted = true, bool reversible = true, bool evidence = true) =>
        new(Relevance: 0.9, HasEvidence: evidence, RiskLevel: "LOW", CostEstimate: cost,
            Permitted: permitted, Reversible: reversible);

    private static DecisionOption Option(
        string mode, OptionEvaluation? evaluation = null, string? silenceReason = null,
        params string[] blocking) =>
        new(mode, $"{mode} rationale", ExpectedEffects: mode == DecisionMode.ToolCall ? ["writes"] : [],
            evaluation ?? Ok(), Prerequisites: [], BlockingReasons: blocking, silenceReason);

    private static DecisionThought Thought(
        IReadOnlyList<DecisionOption> options, double confidence = 0.5,
        IReadOnlyList<string>? evidence = null, bool failing = false) =>
        new(CycleId, "goal/1", options, evidence ?? ["memory/1"], confidence, "LOW", failing);

    private static DecisionContext Context(
        bool motor = true, params string[] silenceReasons) =>
        new(motor, silenceReasons.Length == 0 ? [SilenceReason.NoiseLimit] : silenceReasons);

    // ---- rule 1: the six axes are structural ----

    [Fact]
    public async Task AnOptionCarriesAllSixEvaluationAxes()
    {
        using var db = new SqliteTestDb();

        Decision decision = await Engine(db).EvaluateAsync(
            Thought([Option(DecisionMode.Respond)]), Context(), Ct);

        OptionEvaluation evaluation = decision.SelectedOption.Evaluation;
        Assert.True(evaluation.Relevance > 0);
        Assert.True(evaluation.HasEvidence);
        Assert.False(string.IsNullOrEmpty(evaluation.RiskLevel));
        Assert.True(evaluation.Permitted);
        Assert.True(evaluation.Reversible);
    }

    // ---- never escalate privilege ----

    [Fact]
    public async Task AnOptionThatIsNotPermittedIsDroppedNotGranted()
    {
        using var db = new SqliteTestDb();

        Decision decision = await Engine(db).EvaluateAsync(
            Thought([
                Option(DecisionMode.ToolCall, Ok(permitted: false)),
                Option(DecisionMode.Respond),
            ]), Context(), Ct);

        Assert.Equal(DecisionMode.Respond, decision.Mode);
    }

    [Fact]
    public async Task AnOptionWithBlockingReasonsIsNotSelected()
    {
        using var db = new SqliteTestDb();

        Decision decision = await Engine(db).EvaluateAsync(
            Thought([
                Option(DecisionMode.ToolCall, blocking: "missing credential"),
                Option(DecisionMode.Respond),
            ]), Context(), Ct);

        Assert.Equal(DecisionMode.Respond, decision.Mode);
    }

    // ---- limit case: high confidence without a source ----

    [Fact]
    public async Task HighConfidenceWithNoEvidenceIsReducedToAsking()
    {
        using var db = new SqliteTestDb();

        Decision decision = await Engine(db).EvaluateAsync(
            Thought([Option(DecisionMode.ToolCall), Option(DecisionMode.Ask)],
                confidence: 0.95, evidence: []),
            Context(), Ct);

        Assert.Equal(DecisionMode.Ask, decision.Mode);
        Assert.Contains(decision.Uncertainty, u => u.Contains("no evidence", StringComparison.Ordinal));
    }

    // ---- rule 3: silence is narrow ----

    [Fact]
    public async Task SilenceRequiresAPermittedReasonOnAPermittingChannel()
    {
        using var db = new SqliteTestDb();

        Decision allowed = await Engine(db).EvaluateAsync(
            Thought([Option(DecisionMode.Silent, silenceReason: SilenceReason.NoiseLimit)]),
            Context(silenceReasons: SilenceReason.NoiseLimit), Ct);

        Assert.Equal(DecisionMode.Silent, allowed.Mode);

        Decision refused = await Engine(db).EvaluateAsync(
            Thought([Option(DecisionMode.Silent, silenceReason: SilenceReason.Privacy)]),
            Context(silenceReasons: SilenceReason.NoiseLimit), Ct);

        Assert.NotEqual(DecisionMode.Silent, refused.Mode);
    }

    [Fact]
    public async Task AFailingCycleIsNeverSilent()
    {
        using var db = new SqliteTestDb();

        // Rule 3 names four reasons for silence and hiding a fault is not among them.
        Decision decision = await Engine(db).EvaluateAsync(
            Thought([Option(DecisionMode.Silent, silenceReason: SilenceReason.NoiseLimit)], failing: true),
            Context(silenceReasons: SilenceReason.NoiseLimit), Ct);

        Assert.NotEqual(DecisionMode.Silent, decision.Mode);
        Assert.Contains(decision.Uncertainty, u => u.Contains("silence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnInventedSilenceReasonIsRefused()
    {
        using var db = new SqliteTestDb();

        Decision decision = await Engine(db).EvaluateAsync(
            Thought([Option(DecisionMode.Silent, silenceReason: "NOT_WORTH_MENTIONING")]),
            Context(silenceReasons: "NOT_WORTH_MENTIONING"), Ct);

        Assert.NotEqual(DecisionMode.Silent, decision.Mode);
    }

    // ---- limit case: motor unavailable ----

    [Fact]
    public async Task WhenTheMotorIsDownToolsAreBlockedAndSaidSo()
    {
        using var db = new SqliteTestDb();

        Decision decision = await Engine(db).EvaluateAsync(
            Thought([Option(DecisionMode.ToolCall), Option(DecisionMode.Respond)]),
            Context(motor: false), Ct);

        Assert.Equal(DecisionMode.Respond, decision.Mode);
        Assert.Contains(decision.Uncertainty, u => u.Contains("motor", StringComparison.OrdinalIgnoreCase));
    }

    // ---- limit case: two equivalent options ----

    [Fact]
    public async Task TheSmallerFootprintWins()
    {
        using var db = new SqliteTestDb();

        Decision decision = await Engine(db).EvaluateAsync(
            Thought([Option(DecisionMode.ToolCall, Ok(cost: 1)), Option(DecisionMode.Respond, Ok(cost: 1))]),
            Context(), Ct);

        // Equivalent on cost, so the option that does not reach outside Aurora is preferred.
        Assert.Equal(DecisionMode.Respond, decision.Mode);
    }

    [Fact]
    public async Task TwoEquivalentExternalOptionsBecomeAQuestion()
    {
        using var db = new SqliteTestDb();

        Decision decision = await Engine(db).EvaluateAsync(
            Thought([
                Option(DecisionMode.ToolCall, Ok(cost: 5)) with { RationaleSummary = "send by email" },
                Option(DecisionMode.ToolCall, Ok(cost: 5)) with { RationaleSummary = "send by chat" },
            ]), Context(), Ct);

        Assert.Equal(DecisionMode.Ask, decision.Mode);
    }

    // ---- rule 2: no tool call without an allow ----

    [Fact]
    public async Task ATooCallWithoutAnAllowingPolicyCannotCommit()
    {
        using var db = new SqliteTestDb();
        var engine = Engine(db);
        Decision decision = await engine.EvaluateAsync(
            Thought([Option(DecisionMode.ToolCall)]), Context(), Ct);

        await Assert.ThrowsAsync<DecisionException>(() => engine.CommitAsync(decision.Id, [], Ct));

        await Assert.ThrowsAsync<DecisionException>(() => engine.CommitAsync(
            decision.Id, [new PolicyResult("p1", Allowed: false, ApprovalSatisfied: true)], Ct));
    }

    [Fact]
    public async Task ATooCallRequiringApprovalCannotCommitUntilItIsSatisfied()
    {
        using var db = new SqliteTestDb();
        var engine = Engine(db);
        Decision decision = await engine.EvaluateAsync(
            Thought([Option(DecisionMode.ToolCall)]), Context(), Ct);

        Assert.True(decision.ApprovalRequired);

        await Assert.ThrowsAsync<DecisionException>(() => engine.CommitAsync(
            decision.Id, [new PolicyResult("p1", Allowed: true, ApprovalSatisfied: false)], Ct));

        Decision committed = await engine.CommitAsync(
            decision.Id, [new PolicyResult("p1", Allowed: true, ApprovalSatisfied: true)], Ct);

        Assert.Equal(DecisionState.Committed, committed.Status);
        Assert.Equal(["p1"], committed.PolicyDecisionIds);
    }

    [Fact]
    public async Task ANonToolDecisionCommitsWithoutPolicyResults()
    {
        using var db = new SqliteTestDb();
        var engine = Engine(db);
        Decision decision = await engine.EvaluateAsync(
            Thought([Option(DecisionMode.Respond)]), Context(), Ct);

        Assert.Equal(DecisionState.Committed, (await engine.CommitAsync(decision.Id, [], Ct)).Status);
    }

    // ---- rule 4: expiry and supersession ----

    [Fact]
    public async Task ADecisionPastItsDeadlineExpires()
    {
        using var db = new SqliteTestDb();
        var engine = Engine(db);
        await engine.EvaluateAsync(
            Thought([Option(DecisionMode.Respond)]),
            new DecisionContext(true, [SilenceReason.NoiseLimit], Now.AddMinutes(5).ToString("O")), Ct);

        var later = Engine(db, Now.AddHours(1));

        Assert.Equal(1, await later.ExpireDueAsync(Ct));
    }

    [Fact]
    public async Task AnExpiredDecisionIsNotCommitted()
    {
        using var db = new SqliteTestDb();
        Decision decision = await Engine(db).EvaluateAsync(
            Thought([Option(DecisionMode.Respond)]),
            new DecisionContext(true, [SilenceReason.NoiseLimit], Now.AddMinutes(5).ToString("O")), Ct);

        await Assert.ThrowsAsync<DecisionException>(
            () => Engine(db, Now.AddHours(1)).CommitAsync(decision.Id, [], Ct));
    }

    [Fact]
    public async Task NewInformationSupersedesADecisionBeforeItTakesEffect()
    {
        using var db = new SqliteTestDb();
        var engine = Engine(db);
        Decision decision = await engine.EvaluateAsync(
            Thought([Option(DecisionMode.Respond)]), Context(), Ct);

        Decision superseded = await engine.InvalidateAsync(decision.Id, "the goal changed", Ct);

        Assert.Equal(DecisionState.Superseded, superseded.Status);
        Assert.Contains(superseded.Uncertainty, u => u.Contains("the goal changed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AlternativesAreKeptOnTheRecord()
    {
        using var db = new SqliteTestDb();

        Decision decision = await Engine(db).EvaluateAsync(
            Thought([Option(DecisionMode.Respond), Option(DecisionMode.Ask), Option(DecisionMode.Wait)]),
            Context(), Ct);

        // What was not chosen is part of explaining what was.
        Assert.Equal(2, decision.AlternativesConsidered.Count);
    }
}
