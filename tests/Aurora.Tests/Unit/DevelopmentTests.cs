using System.Globalization;
using System.Text.Json;
using Aurora.Adapters.Development;
using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// The development model (RFC 037): confidence earned, never accrued.
/// </summary>
/// <remarks>
/// The distinction under test throughout: development changes how much of Aurora's own caution
/// sits on top of the rules, and never the rules.
/// </remarks>
public sealed class DevelopmentTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Mind = "mind/local";

    private const string Schema =
        """{"type":"object","additionalProperties":false,"properties":{"message":{"type":"string"}}}""";

    private static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static FakeCapability Reading() =>
        new(FakeCapability.LowReadOnly("clock.now", Schema), _ => JsonDocument.Parse("{}").RootElement);

    private static FakeCapability Writing() =>
        new(
            new CapabilityDescriptor(
                "files.write", "Write a file", "writes inside the sandbox",
                JsonDocument.Parse(Schema).RootElement.Clone(),
                ["files.write"], RiskLevel.Medium, true),
            _ => JsonDocument.Parse("{}").RootElement);

    private sealed record World(
        SqliteDevelopmentModel Development, SqliteAuditStore Audit, TestClock Clock);

    private static World Build(SqliteTestDb db, string now = "2026-01-15T09:00:00+00:00")
    {
        var clock = new TestClock(At(now));
        var audit = new SqliteAuditStore(
            db.Factory, clock, new byte[32],
            new AuditAnchorFile(TestTemp.Path("anchor")));

        return new World(
            new SqliteDevelopmentModel(
                db.Factory, audit,
                new Adapters.Capabilities.StaticCapabilityRegistry([Reading(), Writing()]),
                SqliteDevelopmentModel.DefaultProfile, clock, TestBus.Over(db.Factory, clock)),
            audit, clock);
    }

    private static async Task RecordAsync(World world, string actionId, string outcome, int times)
    {
        for (var i = 0; i < times; i++)
        {
            await world.Audit.AppendAsync(
                new AuditEntry(
                    "c1", "u1", actionId, $"hash-{Guid.NewGuid():N}", outcome,
                    Risk: "Low", Via: "explicit", Decision: "auto_low", PolicyIds: "p"),
                Ct);
        }
    }

    // ---- a new instance starts supervised ----

    [Fact]
    public async Task ANewInstanceStartsSupervisedAndConfirmsEverything()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        DevelopmentState state = await world.Development.CurrentAsync(Mind, Ct);

        // Beginning anywhere else would be granting confidence nothing has been shown to deserve.
        Assert.Equal("stage/supervised", state.CurrentStageId);
        Assert.Equal(DevelopmentStatus.Probation, state.Status);
    }

    // ---- rule 1: evidence, not elapsed time ----

    [Fact]
    public async Task TimePassingIsNotEvidenceOfAnything()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        world.Clock.UtcNow = At("2027-01-15T09:00:00+00:00");

        DevelopmentAssessment assessment = await world.Development.AssessAsync(Mind, Ct);

        // A year of doing nothing says nothing about reliability.
        Assert.False(assessment.ReadyToPromote);
        Assert.Contains(assessment.Missing, m => m.Contains("more successful", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhatIsMissingIsStatedInSomethingSomebodyCanActOn()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        await RecordAsync(world, "clock.now", "completed", 12);

        DevelopmentAssessment assessment = await world.Development.AssessAsync(Mind, Ct);

        // "Not yet" is not an answer somebody can act on; a count is.
        Assert.Contains(assessment.Missing, m => m.Contains("8 more successful Low", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnoughEvidenceAtTheRightLevelSupportsAPromotion()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        await RecordAsync(world, "clock.now", "completed", 20);

        DevelopmentAssessment assessment = await world.Development.AssessAsync(Mind, Ct);

        Assert.True(assessment.ReadyToPromote);
        Assert.Equal("stage/assisting", assessment.NextStageId);
        Assert.Empty(assessment.Missing);
    }

    // ---- the limit case this RFC is really about ----

    [Fact]
    public async Task ManyLowRiskSuccessesDoNotArgueForMediumRiskAutonomy()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        await RecordAsync(world, "clock.now", "completed", 20);
        DevelopmentProposal first = await world.Development.ProposeTransitionAsync(
            Mind, "stage/assisting", Ct);
        await world.Development.ApplyTransitionAsync(first.Id, "approval/1", "paulo", Ct);

        // A hundred more of the same. Twenty successful clock readings are not an argument about
        // writing files, and neither are a hundred and twenty.
        await RecordAsync(world, "clock.now", "completed", 100);

        DevelopmentAssessment assessment = await world.Development.AssessAsync(Mind, Ct);

        Assert.False(assessment.ReadyToPromote);
        Assert.Contains(
            assessment.Missing, m => m.Contains("successful Medium", StringComparison.Ordinal));

        await Assert.ThrowsAsync<DevelopmentException>(() =>
            world.Development.ProposeTransitionAsync(Mind, "stage/trusted", Ct));
    }

    [Fact]
    public async Task EvidenceIsCountedPerRiskLevelAndNeverSummedAcrossThem()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        await RecordAsync(world, "clock.now", "completed", 20);
        await RecordAsync(world, "files.write", "completed", 3);

        DevelopmentAssessment assessment = await world.Development.AssessAsync(Mind, Ct);

        ReliabilityEvidence low = assessment.Evidence.Single(e => e.Risk == RiskLevel.Low);
        ReliabilityEvidence medium = assessment.Evidence.Single(e => e.Risk == RiskLevel.Medium);

        Assert.Equal(20, low.Successes);
        Assert.Equal(3, medium.Successes);
    }

    // ---- stages are moved through one at a time ----

    [Fact]
    public async Task StagesAreNotSkipped()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        await RecordAsync(world, "clock.now", "completed", 20);

        await Assert.ThrowsAsync<DevelopmentException>(() =>
            world.Development.ProposeTransitionAsync(Mind, "stage/trusted", Ct));
    }

    // ---- rule 4: gaining autonomy is the owner's, giving it up is not ----

    [Fact]
    public async Task GainingAutonomyNeedsTheOwnerAndIsWrittenIntoTheAudit()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        await RecordAsync(world, "clock.now", "completed", 20);
        DevelopmentProposal proposal = await world.Development.ProposeTransitionAsync(
            Mind, "stage/assisting", Ct);

        await Assert.ThrowsAsync<DevelopmentException>(() =>
            world.Development.ApplyTransitionAsync(proposal.Id, "", "paulo", Ct));

        DevelopmentState moved = await world.Development.ApplyTransitionAsync(
            proposal.Id, "approval/1", "paulo", Ct);

        Assert.Equal("stage/assisting", moved.CurrentStageId);

        // Visible: a change in how much Aurora does unasked belongs where a person reads, not only
        // in a table they would have to know to look at.
        IReadOnlyList<AuditRecordView> journal = await world.Audit.QueryAsync(0, 200, Ct);
        Assert.Contains(journal, r => r.ActionId == "development.transition");
    }

    [Fact]
    public async Task PullingAutonomyBackNeedsNoApproval()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        await RecordAsync(world, "clock.now", "completed", 20);
        DevelopmentProposal forward = await world.Development.ProposeTransitionAsync(
            Mind, "stage/assisting", Ct);
        await world.Development.ApplyTransitionAsync(forward.Id, "approval/1", "paulo", Ct);

        DevelopmentProposal back = await world.Development.ProposeTransitionAsync(
            Mind, "stage/supervised", Ct);

        // Needing permission to be more careful would be the wrong way round.
        DevelopmentState pulled = await world.Development.ApplyTransitionAsync(
            back.Id, "", "paulo", Ct);

        Assert.Equal("stage/supervised", pulled.CurrentStageId);
    }

    // ---- rule 2: an incident restricts, in the scope it touched ----

    [Fact]
    public async Task AnIncidentRestrictsTheScopeItTouchedAndNotEverythingElse()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        await RecordAsync(world, "clock.now", "completed", 20);
        DevelopmentProposal proposal = await world.Development.ProposeTransitionAsync(
            Mind, "stage/assisting", Ct);
        await world.Development.ApplyTransitionAsync(proposal.Id, "approval/1", "paulo", Ct);

        await world.Development.RestrictAsync(
            Mind, "files.", "observation/9", "a write went to the wrong path", Ct);

        // Confirmed, because that is what went wrong.
        Assert.True(await world.Development.WantsConfirmationAsync(Mind, Writing().Descriptor, Ct));

        // Not confirmed, because reading the clock had nothing to do with it. Pulling everything
        // back for one failure discards what was earned everywhere else.
        Assert.False(await world.Development.WantsConfirmationAsync(Mind, Reading().Descriptor, Ct));
    }

    [Fact]
    public async Task ARestrictedInstanceIsNotPromotedWhateverTheCountsSay()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        await RecordAsync(world, "clock.now", "completed", 40);
        await world.Development.RestrictAsync(Mind, "files.", "observation/9", "a bad write", Ct);

        DevelopmentAssessment assessment = await world.Development.AssessAsync(Mind, Ct);

        Assert.False(assessment.ReadyToPromote);
        Assert.Contains(assessment.Missing, m => m.Contains("RESTRICTED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARestrictionNamesItsScopeAndTheIncidentBehindIt()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        await Assert.ThrowsAsync<DevelopmentException>(() =>
            world.Development.RestrictAsync(Mind, "", "observation/9", "reason", Ct));

        await Assert.ThrowsAsync<DevelopmentException>(() =>
            world.Development.RestrictAsync(Mind, "files.", "", "reason", Ct));
    }

    // ---- development only ever adds caution ----

    [Fact]
    public async Task ASupervisedInstanceConfirmsEvenWhatPolicyWouldAllow()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        // clock.now is LOW and auto-consent. Supervised still wants to be asked, which is
        // development adding its own caution rather than removing anybody else's.
        Assert.False(await world.Development.WantsConfirmationAsync(Mind, Reading().Descriptor, Ct));
        Assert.True(await world.Development.WantsConfirmationAsync(Mind, Writing().Descriptor, Ct));
    }

    [Fact]
    public async Task GrowingUpRemovesAuroraSOwnCautionAndNeverPolicySa()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        await RecordAsync(world, "clock.now", "completed", 20);
        DevelopmentProposal first = await world.Development.ProposeTransitionAsync(
            Mind, "stage/assisting", Ct);
        await world.Development.ApplyTransitionAsync(first.Id, "approval/1", "paulo", Ct);

        await RecordAsync(world, "files.write", "completed", 15);
        DevelopmentProposal second = await world.Development.ProposeTransitionAsync(
            Mind, "stage/trusted", Ct);
        await world.Development.ApplyTransitionAsync(second.Id, "approval/2", "paulo", Ct);

        // At the top stage development has nothing further to ask about a MEDIUM capability. That
        // is not permission: files.write is still ApprovalRequired, and the Kernel still asks.
        Assert.False(await world.Development.WantsConfirmationAsync(Mind, Writing().Descriptor, Ct));
        Assert.True(Writing().Descriptor.ApprovalRequired);
    }

    [Fact]
    public async Task TooManyFailuresHoldTheStageEvenWithEnoughSuccesses()
    {
        using var db = new SqliteTestDb();
        World world = Build(db);

        await RecordAsync(world, "clock.now", "completed", 30);
        await RecordAsync(world, "clock.now", "failed", 2);

        DevelopmentAssessment assessment = await world.Development.AssessAsync(Mind, Ct);

        // The supervised stage allows none. Confidence grows through evidence and shrinks through
        // risk, and a failure is evidence too.
        Assert.False(assessment.ReadyToPromote);
        Assert.Contains(assessment.Missing, m => m.Contains("failure(s) at Low", StringComparison.Ordinal));
    }
}
