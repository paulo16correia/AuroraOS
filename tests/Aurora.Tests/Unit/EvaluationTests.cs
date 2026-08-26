using System.Text.Json;
using Aurora.Adapters.Observations;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// RFC 08's evaluator: nothing that changes how Aurora behaves is applied untested.
/// </summary>
/// <remarks>
/// The separation the RFC exists to keep — observe, propose, test, apply — only holds if the third
/// step is real. Before this it was not: <c>TESTING</c> was a declared state that nothing ever
/// entered, and a proposal went from approval straight to deployment.
/// </remarks>
public sealed class EvaluationTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");

    private static SqliteObservationService New(SqliteTestDb db) => new(db.Factory, new TestClock(Now));

    private static LearningProposal Proposal(
        string type = LearningProposalType.Procedure,
        string changeSet = """{"backoff":"exponential"}""",
        string risk = LearningRisk.Medium,
        string evaluationPlan = "shadow for one week",
        string rollbackPlan = "restore the previous procedure") => new(
        string.Empty, string.Empty, type, changeSet, evaluationPlan, rollbackPlan,
        LearningProposalState.Proposed, "fewer eager retries", risk);

    /// <summary>Walks a proposal to APPROVED, which is where evaluation starts.</summary>
    private static async Task<string> ApprovedAsync(
        SqliteObservationService service, LearningProposal proposal)
    {
        AuroraAction action = await service.ProposeActionAsync(
            "decision/1", "message.send", "person/paulo", "hash-1", true, Ct);

        await service.AuthorizeActionAsync(action.Id, Ct);
        await service.DispatchActionAsync(action.Id, "call/1", Ct);

        Observation raw = await service.RecordAsync(
            action.Id, "kernel", "tool", ObservationOutcome.Failure, null, null, Ct);

        await service.ValidateAsync(raw.Id, valid: true, null, Ct);

        Reflection reflection = await service.ReflectAsync(
            raw.Id, "retry policy is too eager", ["back off sooner"], [proposal], Ct);

        var proposalId = reflection.ProposalRefs[0];
        await service.DecideLearningAsync(proposalId, approve: true, Ct);

        return proposalId;
    }

    private static IReadOnlyList<JsonElement> Metrics(EvaluationRun run) =>
        JsonDocument.Parse(run.MetricsJson).RootElement.EnumerateArray().ToList();

    private static JsonElement Dimension(EvaluationRun run, string name) =>
        Metrics(run).Single(m => m.GetProperty("dimension").GetString() == name);

    [Fact]
    public async Task AnEvaluationMeasuresSecurityCostAndPrivacy()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        var proposalId = await ApprovedAsync(service, Proposal());

        EvaluationRun run = await service.EvaluateAsync(proposalId, "the retry path", Ct);

        // Rule 4 names three dimensions and this is all three of them, every time. A dimension
        // that is absent from the metrics is a dimension nobody will notice was never checked.
        Assert.Equal(
            ["security_regression", "cost", "privacy"],
            Metrics(run).Select(m => m.GetProperty("dimension").GetString()));

        Assert.Equal(EvaluationVerdict.Pass, run.Verdict);
        Assert.Equal("the retry path", run.TestScope);

        // What it ran against, so the verdict can be read against its evidence rather than trusted.
        Assert.False(string.IsNullOrWhiteSpace(run.DatasetRef));
    }

    [Fact]
    public async Task ADimensionAuroraCannotMeasureIsInconclusiveRatherThanAPass()
    {
        using var db = new SqliteTestDb();
        var service = New(db);

        // No evaluation plan: the proposal has told the evaluator nothing to measure cost against.
        var proposalId = await ApprovedAsync(service, Proposal(evaluationPlan: ""));

        EvaluationRun run = await service.EvaluateAsync(proposalId, "anything", Ct);

        // The whole point. A system that reports PASS for what it did not look at is worse than
        // one with no evaluator, because the pass is then cited as evidence.
        Assert.Equal(EvaluationVerdict.Inconclusive, run.Verdict);
        Assert.False(Dimension(run, "cost").GetProperty("measured").GetBoolean());
        Assert.False(Dimension(run, "cost").GetProperty("regressed").GetBoolean());

        // And "unmeasured" is not "fine": it does not read as a regression either, because it is
        // neither, and collapsing the two would make every unmeasurable proposal look dangerous.
        Assert.True(Dimension(run, "privacy").GetProperty("measured").GetBoolean());
    }

    [Fact]
    public async Task AChangeCarryingACredentialFails()
    {
        using var db = new SqliteTestDb();
        var service = New(db);

        var proposalId = await ApprovedAsync(service, Proposal(
            changeSet: """{"note":"use this","token":"ghp_A1b2C3d4E5f6G7h8"}"""));

        EvaluationRun run = await service.EvaluateAsync(proposalId, "anything", Ct);

        Assert.Equal(EvaluationVerdict.Fail, run.Verdict);
        Assert.True(Dimension(run, "privacy").GetProperty("regressed").GetBoolean());

        // A failed evaluation ends the proposal rather than leaving it available to apply.
        await Assert.ThrowsAsync<ObservationException>(
            () => service.ApplyLearningAsync(proposalId, Ct));
    }

    [Fact]
    public async Task AMemoryChangeThatReachesForAuthorityIsASecurityRegression()
    {
        using var db = new SqliteTestDb();
        var service = New(db);

        // Declared as a memory change; the change set is about policy. This is the substitution
        // that matters — a proposal whose declared type says harmless and whose content is not.
        var proposalId = await ApprovedAsync(service, Proposal(
            type: LearningProposalType.Memory,
            changeSet: """{"lesson":"grant the mailer capability by default"}"""));

        EvaluationRun run = await service.EvaluateAsync(proposalId, "anything", Ct);

        Assert.Equal(EvaluationVerdict.Fail, run.Verdict);
        Assert.True(Dimension(run, "security_regression").GetProperty("regressed").GetBoolean());
    }

    [Fact]
    public async Task AProcedureChangeIsNotAppliedUntilItHasBeenTested()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        var proposalId = await ApprovedAsync(service, Proposal());

        // Approved, and that is not enough: rule 3 asks for approved, tested and reversible.
        ObservationException untested = await Assert.ThrowsAsync<ObservationException>(
            () => service.ApplyLearningAsync(proposalId, Ct));

        Assert.Contains("never has been", untested.Message, StringComparison.Ordinal);

        await service.EvaluateAsync(proposalId, "the retry path", Ct);
        LearningProposal deployed = await service.ApplyLearningAsync(proposalId, Ct);

        Assert.Equal(LearningProposalState.Deployed, deployed.State);
    }

    [Fact]
    public async Task AnInconclusiveEvaluationWaitsForAPersonRatherThanApplyingItself()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        var proposalId = await ApprovedAsync(service, Proposal(evaluationPlan: ""));

        EvaluationRun run = await service.EvaluateAsync(proposalId, "anything", Ct);
        Assert.Equal(EvaluationVerdict.Inconclusive, run.Verdict);

        // RFC 08's limit case: keep it in test and require a human decision.
        ObservationException held = await Assert.ThrowsAsync<ObservationException>(
            () => service.ApplyLearningAsync(proposalId, Ct));

        Assert.Contains("human decision", held.Message, StringComparison.Ordinal);

        LearningProposal accepted = await service.ApplyLearningAsync(
            proposalId, Ct, acceptInconclusive: true);

        Assert.Equal(LearningProposalState.Deployed, accepted.State);
    }

    [Fact]
    public async Task AChangeWithNoWayBackIsNotApplied()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        var proposalId = await ApprovedAsync(service, Proposal(rollbackPlan: ""));

        await service.EvaluateAsync(proposalId, "anything", Ct);

        // Reversible is one of rule 3's three conditions, not a nice-to-have alongside them.
        ObservationException irreversible = await Assert.ThrowsAsync<ObservationException>(
            () => service.ApplyLearningAsync(proposalId, Ct));

        Assert.Contains("rollback", irreversible.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALowRiskMemoryChangeStillGoesStraightThrough()
    {
        using var db = new SqliteTestDb();
        var service = New(db);

        var proposalId = await ApprovedAsync(service, Proposal(
            type: LearningProposalType.Memory,
            changeSet: """{"lesson":"back off sooner"}""",
            risk: LearningRisk.Low));

        // Rule 2 is a permission, not an oversight, and the gate above must not have swallowed it:
        // a memory is provenanced, revisable and forgettable, so getting one wrong is a correction
        // rather than a change in behaviour.
        LearningProposal deployed = await service.ApplyLearningAsync(proposalId, Ct);

        Assert.Equal(LearningProposalState.Deployed, deployed.State);
    }

    [Fact]
    public async Task EveryEvaluationIsKeptRatherThanOverwritten()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        var proposalId = await ApprovedAsync(service, Proposal(evaluationPlan: ""));

        await service.EvaluateAsync(proposalId, "first look", Ct);
        await service.EvaluateAsync(proposalId, "second look", Ct);

        IReadOnlyList<EvaluationRun> runs = await service.EvaluationsAsync(proposalId, Ct);

        // Which verdict was current when a change was applied is the question somebody asks after
        // it goes wrong, and it cannot be answered by a row that was overwritten.
        Assert.Equal(["first look", "second look"], runs.Select(r => r.TestScope));
    }
}
