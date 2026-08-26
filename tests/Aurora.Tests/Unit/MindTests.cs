using Aurora.Adapters.Minds;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// RFC 020: Aurora's persistent state belongs to a Mind, and the Mind's own fields change through
/// a validated change set rather than a setter.
/// </summary>
/// <remarks>
/// Rule 1 says no external module writes directly to Mind. Aurora enforced the spirit of that
/// through LAW-001 for memories and beliefs, which have their own guarded write paths, and had no
/// change set at all — so the Mind's own fields had no write path because there was no Mind.
/// </remarks>
public sealed class MindTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");

    private static SqliteMindService New(SqliteTestDb db) => new(db.Factory, new TestClock(Now));

    private static MindChangeSet Draft(
        string mindId,
        IReadOnlyList<MindChange>? changes = null,
        IReadOnlyList<string>? evidence = null,
        string source = MindChangeSource.Operator) => new(
        string.Empty, mindId, source,
        changes ?? [new MindChange(MindField.PolicySetVersion, "2")],
        evidence ?? ["adr/0058"], ["policy/1"],
        MindChangeSetStatus.Proposed, string.Empty);

    [Fact]
    public async Task OpeningTheSameTenantTwiceReturnsTheSameMind()
    {
        using var db = new SqliteTestDb();
        var service = New(db);

        Mind first = await service.OpenAsync(Tenant.Local, Ct);
        Mind again = await service.OpenAsync(Tenant.Local, Ct);

        // One Mind per tenant, which is the same boundary LAW-005 draws for everything else.
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(MindStatus.Active, first.Status);
    }

    [Fact]
    public async Task AChangeSetIsProposedValidatedAndOnlyThenApplied()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        Mind mind = await service.OpenAsync(Tenant.Local, Ct);

        MindChangeSet proposed = await service.ProposeAsync(Draft(mind.Id), Ct);
        Assert.Equal(MindChangeSetStatus.Proposed, proposed.Status);

        // Proposing is not authorising: nothing moved.
        Assert.Equal("0", (await service.GetAsync(mind.Id, Ct))!.PolicySetVersion);

        // And applying skips no step.
        await Assert.ThrowsAsync<MindException>(() => service.ApplyAsync(proposed.Id, Ct));

        MindChangeSet validated = await service.ValidateAsync(proposed.Id, Ct);
        Assert.Equal(MindChangeSetStatus.Validated, validated.Status);

        Mind applied = await service.ApplyAsync(proposed.Id, Ct);
        Assert.Equal("2", applied.PolicySetVersion);

        Assert.Equal(
            MindChangeSetStatus.Applied,
            (await service.ChangeSetAsync(proposed.Id, Ct))!.Status);
    }

    [Fact]
    public async Task AChangeSetWithNoEvidenceIsRejectedAndSaysWhy()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        Mind mind = await service.OpenAsync(Tenant.Local, Ct);

        MindChangeSet proposed = await service.ProposeAsync(Draft(mind.Id, evidence: []), Ct);
        MindChangeSet decided = await service.ValidateAsync(proposed.Id, Ct);

        // LAW-001 in the Mind's own terms: nothing enters without something behind it.
        Assert.Equal(MindChangeSetStatus.Rejected, decided.Status);
        Assert.Contains("no evidence", decided.Detail!, StringComparison.Ordinal);

        // Rejected is a terminal answer, not a suggestion to try applying anyway.
        await Assert.ThrowsAsync<MindException>(() => service.ApplyAsync(proposed.Id, Ct));
    }

    [Fact]
    public async Task AChangeToSomethingThatIsNotAFieldOfTheMindIsRejected()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        Mind mind = await service.OpenAsync(Tenant.Local, Ct);

        // Memories, beliefs and relationships are owned by their own services and have their own
        // provenance-enforced write paths. Routing them through here would be a second way in.
        MindChangeSet proposed = await service.ProposeAsync(
            Draft(mind.Id, changes: [new MindChange("belief_ids", "belief/1")]), Ct);

        MindChangeSet decided = await service.ValidateAsync(proposed.Id, Ct);

        Assert.Equal(MindChangeSetStatus.Rejected, decided.Status);
        Assert.Contains("is not a field of the Mind", decided.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneFieldChangedTwiceInOneSetIsRejected()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        Mind mind = await service.OpenAsync(Tenant.Local, Ct);

        MindChangeSet proposed = await service.ProposeAsync(
            Draft(mind.Id, changes:
            [
                new MindChange(MindField.PolicySetVersion, "2"),
                new MindChange(MindField.PolicySetVersion, "3"),
            ]), Ct);

        MindChangeSet decided = await service.ValidateAsync(proposed.Id, Ct);

        // Which of the two wins would depend on the order they were listed in, and that is not a
        // decision anybody made.
        Assert.Equal(MindChangeSetStatus.Rejected, decided.Status);
        Assert.Contains("changed twice", decided.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryFieldMovesTogetherOrNoneDoes()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        Mind mind = await service.OpenAsync(Tenant.Local, Ct);

        MindChangeSet proposed = await service.ProposeAsync(
            Draft(mind.Id, changes:
            [
                new MindChange(MindField.PolicySetVersion, "2"),
                new MindChange(MindField.WorldModelVersion, "7"),
                new MindChange(MindField.SelfModelId, "self/1"),
            ]), Ct);

        await service.ValidateAsync(proposed.Id, Ct);
        Mind applied = await service.ApplyAsync(proposed.Id, Ct);

        // Rule 2: atomic per aggregate. Three fields, one transaction, one updated_at.
        Assert.Equal("2", applied.PolicySetVersion);
        Assert.Equal("7", applied.WorldModelVersion);
        Assert.Equal("self/1", applied.SelfModelId);
    }

    [Fact]
    public async Task AnUnknownSourceIsRefusedAtTheDoor()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        Mind mind = await service.OpenAsync(Tenant.Local, Ct);

        // Rule 1 is about knowing who wrote to Mind. An unrecognised source is an answer nobody
        // can act on later.
        await Assert.ThrowsAsync<MindException>(
            () => service.ProposeAsync(Draft(mind.Id, source: "SOMEBODY"), Ct));
    }

    [Fact]
    public async Task PausingRecordsWhoAndWhyAndLeavesTheMindReadable()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        Mind mind = await service.OpenAsync(Tenant.Local, Ct);

        await Assert.ThrowsAsync<MindException>(
            () => service.PauseAsync(mind.Id, "owner", "  ", Ct));

        Mind paused = await service.PauseAsync(mind.Id, "owner", "going away for a week", Ct);

        Assert.Equal(MindStatus.Paused, paused.Status);
        Assert.Equal("owner", paused.PausedBy);

        // Rule 3 is a restriction on acting, not a blackout: it still reads back.
        Assert.Equal(MindStatus.Paused, (await service.GetAsync(mind.Id, Ct))!.Status);

        Mind resumed = await service.ResumeAsync(mind.Id, "owner", Ct);
        Assert.Equal(MindStatus.Active, resumed.Status);
        Assert.Null(resumed.PausedReason);
    }

    [Fact]
    public async Task AChangeSetThatChangesNothingIsNotAProposal()
    {
        using var db = new SqliteTestDb();
        var service = New(db);
        Mind mind = await service.OpenAsync(Tenant.Local, Ct);

        await Assert.ThrowsAsync<MindException>(
            () => service.ProposeAsync(Draft(mind.Id, changes: []), Ct));
    }
}
