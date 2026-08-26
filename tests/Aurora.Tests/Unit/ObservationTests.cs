using Aurora.Adapters.Observations;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>Conformance tests for LAW-003 and the RFC 040 action/observation objects.</summary>
public sealed class ObservationTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");

    private static SqliteObservationService New(SqliteTestDb db) =>
        new(db.Factory, new TestClock(Now));

    private static Task<AuroraAction> ProposeAsync(SqliteObservationService service) =>
        service.ProposeActionAsync("decision/1", "message.send", "person/paulo", "hash-1", true, Ct);

    private static async Task<AuroraAction> DispatchedAsync(SqliteObservationService service)
    {
        AuroraAction action = await ProposeAsync(service);
        await service.AuthorizeActionAsync(action.Id, Ct);
        return await service.DispatchActionAsync(action.Id, "call/1", Ct);
    }

    // ---- the action state machine ----

    [Fact]
    public async Task AnActionFollowsProposeAuthorizeDispatch()
    {
        using var db = new SqliteTestDb();
        var service = New(db);

        AuroraAction dispatched = await DispatchedAsync(service);

        Assert.Equal(ActionState.Dispatched, dispatched.State);
        Assert.Equal("call/1", dispatched.ToolCallId);
    }

    [Fact]
    public async Task AnActionCannotSkipAuthorization()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        AuroraAction action = await ProposeAsync(service);

        await Assert.ThrowsAsync<ObservationException>(
            () => service.DispatchActionAsync(action.Id, null, Ct));
    }

    [Fact]
    public async Task AnUndispatchedActionCannotBeObserved()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        AuroraAction action = await ProposeAsync(service);

        // An observation of something that never left is a fiction.
        await Assert.ThrowsAsync<ObservationException>(() => service.RecordAsync(
            action.Id, "kernel", "tool", ObservationOutcome.Success, null, null, Ct));
    }

    // ---- LAW-003: no OBSERVED without an observation ----

    [Fact]
    public async Task AnActionCannotCloseWithoutAnObservation()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        AuroraAction dispatched = await DispatchedAsync(service);

        await Assert.ThrowsAsync<ObservationException>(() => service.ObserveAsync(dispatched.Id, Ct));
    }

    [Fact]
    public async Task ARawObservationDoesNotCloseAnAction()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        AuroraAction dispatched = await DispatchedAsync(service);
        await service.RecordAsync(
            dispatched.Id, "kernel", "tool", ObservationOutcome.Success, null, null, Ct);

        // Closing the loop on something unread is not closing the loop.
        await Assert.ThrowsAsync<ObservationException>(() => service.ObserveAsync(dispatched.Id, Ct));
    }

    [Fact]
    public async Task ARejectedObservationDoesNotCloseAnAction()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        AuroraAction dispatched = await DispatchedAsync(service);
        Observation raw = await service.RecordAsync(
            dispatched.Id, "kernel", "tool", ObservationOutcome.Success, null, null, Ct);
        await service.ValidateAsync(raw.Id, valid: false, "payload did not match the contract", Ct);

        await Assert.ThrowsAsync<ObservationException>(() => service.ObserveAsync(dispatched.Id, Ct));
    }

    [Fact]
    public async Task AValidatedObservationClosesTheAction()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        AuroraAction dispatched = await DispatchedAsync(service);
        Observation raw = await service.RecordAsync(
            dispatched.Id, "kernel", "tool", ObservationOutcome.Success, "payload/1", "remote/9", Ct);
        await service.ValidateAsync(raw.Id, valid: true, null, Ct);

        AuroraAction observed = await service.ObserveAsync(dispatched.Id, Ct);

        Assert.Equal(ActionState.Observed, observed.State);
    }

    [Fact]
    public async Task ARejectedObservationRecordsWhy()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        AuroraAction dispatched = await DispatchedAsync(service);
        Observation raw = await service.RecordAsync(
            dispatched.Id, "kernel", "tool", ObservationOutcome.Failure, null, null, Ct);

        await Assert.ThrowsAsync<ObservationException>(
            () => service.ValidateAsync(raw.Id, valid: false, "   ", Ct));

        Observation rejected = await service.ValidateAsync(raw.Id, valid: false, "schema mismatch", Ct);
        Assert.Equal(ObservationState.Rejected, rejected.State);
        Assert.Equal("schema mismatch", rejected.RejectionReason);
    }

    // ---- unknown is not success ----

    [Fact]
    public async Task AnUnknownOutcomeMovesTheActionToUnknownAndKeepsItThere()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        AuroraAction dispatched = await DispatchedAsync(service);
        Observation raw = await service.RecordAsync(
            dispatched.Id, "kernel", "tool", ObservationOutcome.Unknown, null, "remote/9", Ct);
        await service.ValidateAsync(raw.Id, valid: true, null, Ct);

        Assert.Equal(ActionState.Unknown, (await service.GetActionAsync(dispatched.Id, Ct))!.State);

        // "We never found out" is not a completed action.
        await Assert.ThrowsAsync<ObservationException>(() => service.ObserveAsync(dispatched.Id, Ct));
    }

    [Fact]
    public async Task AnUnknownActionClosesOnceSomethingIsActuallyLearned()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        AuroraAction dispatched = await DispatchedAsync(service);
        Observation first = await service.RecordAsync(
            dispatched.Id, "kernel", "tool", ObservationOutcome.Unknown, null, "remote/9", Ct);
        await service.ValidateAsync(first.Id, valid: true, null, Ct);

        Observation second = await service.RecordAsync(
            dispatched.Id, "kernel", "reconcile", ObservationOutcome.Success, null, "remote/9", Ct);
        await service.ValidateAsync(second.Id, valid: true, null, Ct);

        Assert.Equal(ActionState.Observed, (await service.ObserveAsync(dispatched.Id, Ct)).State);
    }

    [Fact]
    public async Task UnobservedActionsAreExposedForReconciliation()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        await DispatchedAsync(service);

        // The scheduler and UI must be able to see what never came back.
        Assert.Single(await service.UnobservedAsync(Ct));
    }

    [Fact]
    public async Task AnUnrecognisedOutcomeIsRefused()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        AuroraAction dispatched = await DispatchedAsync(service);

        await Assert.ThrowsAsync<ObservationException>(() => service.RecordAsync(
            dispatched.Id, "kernel", "tool", "PROBABLY_FINE", null, null, Ct));
    }

    // ---- reflection ----

    [Fact]
    public async Task AnUnvalidatedObservationIsNotReflectedOn()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        AuroraAction dispatched = await DispatchedAsync(service);
        Observation raw = await service.RecordAsync(
            dispatched.Id, "kernel", "tool", ObservationOutcome.Success, null, null, Ct);

        await Assert.ThrowsAsync<ObservationException>(
            () => service.ReflectAsync(raw.Id, "fine", [], [], Ct));
    }

    [Fact]
    public async Task AReflectionWithNoLessonsIsStillARecord()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        AuroraAction dispatched = await DispatchedAsync(service);
        Observation raw = await service.RecordAsync(
            dispatched.Id, "kernel", "tool", ObservationOutcome.Success, null, null, Ct);
        await service.ValidateAsync(raw.Id, valid: true, null, Ct);

        // "Nothing to learn" is a conclusion, and a system that only records interesting outcomes
        // has no baseline to compare them against.
        Reflection reflection = await service.ReflectAsync(raw.Id, "no learning", [], [], Ct);

        Assert.Equal(ReflectionState.Draft, reflection.State);
        Assert.Empty(reflection.Lessons);
        Assert.Equal([raw.Id], reflection.EvidenceRefs);
    }

    // ---- learning applies only what was approved ----

    [Fact]
    public async Task AnUnapprovedChangeIsNotApplied()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        AuroraAction dispatched = await DispatchedAsync(service);
        Observation raw = await service.RecordAsync(
            dispatched.Id, "kernel", "tool", ObservationOutcome.Failure, null, null, Ct);
        await service.ValidateAsync(raw.Id, valid: true, null, Ct);

        Reflection reflection = await service.ReflectAsync(
            raw.Id, "retry policy is too eager", ["back off sooner"],
            [Proposal()], Ct);

        var proposalId = reflection.ProposalRefs[0];

        // A system that deploys its own suggestions is not learning; it is drifting. This one is
        // a low-risk memory change, so approval is the only thing standing between it and being
        // applied — and it is enough to stop it.
        await Assert.ThrowsAsync<ObservationException>(() => service.ApplyLearningAsync(proposalId, Ct));

        await service.DecideLearningAsync(proposalId, approve: true, Ct);
        LearningProposal deployed = await service.ApplyLearningAsync(proposalId, Ct);

        Assert.Equal(LearningProposalState.Deployed, deployed.State);
    }

    [Fact]
    public async Task ARejectedChangeIsNotApplied()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        AuroraAction dispatched = await DispatchedAsync(service);
        Observation raw = await service.RecordAsync(
            dispatched.Id, "kernel", "tool", ObservationOutcome.Failure, null, null, Ct);
        await service.ValidateAsync(raw.Id, valid: true, null, Ct);
        Reflection reflection = await service.ReflectAsync(raw.Id, "hmm", [], [Proposal()], Ct);

        await service.DecideLearningAsync(reflection.ProposalRefs[0], approve: false, Ct);

        await Assert.ThrowsAsync<ObservationException>(
            () => service.ApplyLearningAsync(reflection.ProposalRefs[0], Ct));
    }

    [Fact]
    public async Task AReflectionIsAcceptedOrRejectedOnce()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        AuroraAction dispatched = await DispatchedAsync(service);
        Observation raw = await service.RecordAsync(
            dispatched.Id, "kernel", "tool", ObservationOutcome.Success, null, null, Ct);
        await service.ValidateAsync(raw.Id, valid: true, null, Ct);
        Reflection reflection = await service.ReflectAsync(raw.Id, "fine", [], [], Ct);

        Assert.Equal(
            ReflectionState.Accepted,
            (await service.DecideReflectionAsync(reflection.Id, accept: true, Ct)).State);

        await Assert.ThrowsAsync<ObservationException>(
            () => service.DecideReflectionAsync(reflection.Id, accept: false, Ct));
    }

    /// <summary>
    /// A low-risk memory change: the one kind RFC 08 rule 2 lets through on approval alone.
    /// </summary>
    private static LearningProposal Proposal() => new(
        string.Empty, string.Empty, LearningProposalType.Memory, """{"lesson":"back off sooner"}""",
        "compare against the last week of observations", "forget the memory",
        LearningProposalState.Proposed, "fewer eager retries", LearningRisk.Low);
}
