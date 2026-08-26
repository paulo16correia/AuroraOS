using System.Globalization;
using Aurora.Adapters.Beliefs;
using Aurora.Adapters.Capabilities;
using Aurora.Adapters.Development;
using Aurora.Adapters.Events;
using Aurora.Adapters.Knowledge;
using Aurora.Adapters.LifeHistory;
using Aurora.Adapters.Memories;
using Aurora.Adapters.Missions;
using Aurora.Adapters.Operations;
using Aurora.Adapters.Personality;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Plugins.Sandboxes;
using Aurora.Adapters.Planning;
using Aurora.Adapters.Relationships;
using Aurora.Adapters.Resources;
using Aurora.Adapters.Scheduling;
using Aurora.Adapters.Self;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// LAW-007 across the platform, not only at the bus.
/// </summary>
/// <remarks>
/// ADR 0027 recorded this law as half-satisfied: the outbox enforced its contract while the
/// components that change state said nothing. These tests exercise each producer through its real
/// path and assert the event actually arrives — "the code has a PublishAsync in it" is not the
/// same claim.
/// <para>
/// Every assertion also checks what the payload does <b>not</b> carry. A state change is announced
/// so consumers can react; it is not an excuse to copy the content onto a channel more things read
/// than read the store.
/// </para>
/// </remarks>
public sealed class Law007ProducerTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>The real bus with the real catalogue: an undeclared event would be refused here.</summary>
    private static SqliteEventBus Bus(SqliteTestDb db, IClock clock) =>
        new(db.Factory, new SqliteOutbox(new DeclaredEventCatalogue(), clock), clock);

    private static async Task<IReadOnlyList<DomainEvent>> PublishedAsync(SqliteEventBus bus) =>
        (await bus.ReadAsync(0, 200, Sensitivity.Private, Ct)).Select(e => e.Event).ToList();

    private static async Task<DomainEvent> SingleAsync(SqliteEventBus bus, string type)
    {
        IReadOnlyList<DomainEvent> published = await PublishedAsync(bus);
        return Assert.Single(published, e => e.Type == type);
    }

    // ---- missions ----

    [Fact]
    public async Task AMissionChangingStatusIsAnnouncedWithoutItsPurpose()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));
        SqliteEventBus bus = Bus(db, clock);

        var missions = new SqliteMissionService(
            db.Factory, new SqlitePlanner(db.Factory, clock, bus), bus, clock);

        Mission mission = await missions.CreateAsync(
            new MissionDefinition(
                "keep the owner organised", "a purpose nobody else needs to read",
                "they stop being surprised", ["reads nothing on their behalf"], "paulo"),
            "approval/1", Ct);

        await missions.PauseAsync(mission.Id, "paulo", Ct);

        DomainEvent published = await SingleAsync(bus, EventCatalogue.MissionChanged);

        Assert.Contains(mission.Id, published.PayloadJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("nobody else needs to read", published.PayloadJson!, StringComparison.Ordinal);
    }

    // ---- beliefs ----

    [Fact]
    public async Task AChallengedBeliefIsAnnouncedWithoutTheClaim()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));
        SqliteEventBus bus = Bus(db, clock);
        var beliefs = new SqliteBeliefSystem(db.Factory, BeliefPolicy.Default, clock, bus);

        Belief belief = await beliefs.ProposeAsync(
            new BeliefCandidate(
                "person/owner", "prefers", """{"style":"a claim not for broadcasting"}""",
                BeliefBasis.Observed, 0.8),
            ["conversation/1"], Ct);

        await beliefs.ChallengeAsync(belief.Id, "conversation/40", "contradicted", Ct);

        DomainEvent published = await SingleAsync(bus, EventCatalogue.BeliefChallenged);

        Assert.Contains(belief.Id, published.PayloadJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("not for broadcasting", published.PayloadJson!, StringComparison.Ordinal);
    }

    // ---- relationships ----

    [Fact]
    public async Task ARelationshipEndingIsAnnouncedWithoutNamingWhoItWasWith()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));
        SqliteEventBus bus = Bus(db, clock);

        var relationships = new SqliteRelationshipModel(
            db.Factory,
            new SqliteKnowledgeGraph(
                db.Factory,
                new SqliteMemoryService(db.Factory, new LexicalMemoryRanker(), bus, clock), clock),
            clock, bus);

        RelationshipAssertion tie = await relationships.AssertAsync(
            new RelationshipCandidate(
                "person/owner", RelationType.Professional, "org/verysecretclient", 0.9),
            ["contract/1"], Ct);

        await relationships.EndAsync(tie.Id, "termination/1", Ct);

        DomainEvent published = await SingleAsync(bus, EventCatalogue.RelationshipEnded);

        // A third party's name is not something to broadcast because their relationship ended.
        Assert.DoesNotContain("verysecretclient", published.PayloadJson!, StringComparison.Ordinal);
    }

    // ---- development ----

    [Fact]
    public async Task AChangeInAutonomyIsAnnouncedAndSaysWhichWayItWent()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));
        SqliteEventBus bus = Bus(db, clock);

        var audit = new SqliteAuditStore(
            db.Factory, clock, new byte[32],
            new AuditAnchorFile(Path.Combine(Path.GetTempPath(), $"a-{Guid.NewGuid():N}")));

        var development = new SqliteDevelopmentModel(
            db.Factory, audit,
            new StaticCapabilityRegistry([FakeClock()]),
            SqliteDevelopmentModel.DefaultProfile, clock, bus);

        for (var i = 0; i < 20; i++)
        {
            await audit.AppendAsync(
                new AuditEntry(
                    "c1", "u1", "clock.now", $"h{i}", "completed",
                    Risk: "Low", Via: "explicit", Decision: "auto_low", PolicyIds: "p"),
                Ct);
        }

        DevelopmentProposal proposal = await development.ProposeTransitionAsync(
            "mind/local", "stage/assisting", Ct);
        await development.ApplyTransitionAsync(proposal.Id, "approval/1", "paulo", Ct);

        DomainEvent published = await SingleAsync(bus, EventCatalogue.DevelopmentStageChanged);

        Assert.Contains("grew", published.PayloadJson!, StringComparison.Ordinal);
    }

    // ---- life history ----

    [Fact]
    public async Task AVerifiedEpisodeIsAnnouncedWithoutItsNarrative()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));
        SqliteEventBus bus = Bus(db, clock);

        var audit = new SqliteAuditStore(
            db.Factory, clock, new byte[32],
            new AuditAnchorFile(Path.Combine(Path.GetTempPath(), $"a-{Guid.NewGuid():N}")));

        await audit.AppendAsync(
            new AuditEntry(
                "c1", "u1", "clock.now", "h", "completed",
                Risk: "Low", Via: "explicit", Decision: "auto_low", PolicyIds: "p"),
            Ct);

        var evidence = (await audit.QueryAsync(0, 10, Ct))[^1].RecordId;
        var history = new SqliteLifeHistory(db.Factory, audit, clock, bus);

        LifeEpisode episode = await history.ProposeAsync(
            new LifeEpisode(
                "", "mind/local", EpisodeKind.Birth, "2026-01-15T09:00:00.0000000+00:00", null,
                "a title", "a narrative nobody asked the bus for", [evidence],
                Significance.High, EpisodeStatus.Candidate, Sensitivity.Private, ""),
            Ct);

        await history.VerifyAsync(episode.Id, Ct);

        DomainEvent published = await SingleAsync(bus, EventCatalogue.LifeEpisodeVerified);

        Assert.DoesNotContain("nobody asked the bus", published.PayloadJson!, StringComparison.Ordinal);
    }

    // ---- planner ----

    [Fact]
    public async Task AGoalComingIntoExistenceIsAnnouncedWithoutItsOutcomeText()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));
        SqliteEventBus bus = Bus(db, clock);
        var planner = new SqlitePlanner(db.Factory, clock, bus);

        Goal goal = await planner.DraftAsync(
            new GoalRequest("a title", "an outcome the person phrased themselves", "paulo", [], []), Ct);

        DomainEvent published = await SingleAsync(bus, EventCatalogue.GoalDrafted);

        Assert.Contains(goal.Id, published.PayloadJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("phrased themselves", published.PayloadJson!, StringComparison.Ordinal);
    }

    // ---- identity ----

    [Fact]
    public async Task ActivatingAnIdentityIsAnnouncedSoNothingRendersWithAStaleProfile()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));
        SqliteEventBus bus = Bus(db, clock);

        var relationships = new SqliteRelationshipModel(
            db.Factory,
            new SqliteKnowledgeGraph(
                db.Factory,
                new SqliteMemoryService(db.Factory, new LexicalMemoryRanker(), bus, clock), clock),
            clock, bus);

        var personality = new SqlitePersonalityService(db.Factory, relationships, clock, bus);

        PersonalityProfile draft = await personality.ProposeAsync(
            new PersonalityProfile(
                "", 0, "Aurora", ["pt-PT"], "pt-PT", Voice.Default, ["a value"], ["I feel"],
                ["a rule"], "Aurora is software.", ["defer"], "", null, ProfileStatus.Draft),
            Ct);

        await personality.ActivateAsync(draft.Id, "approval/1", "paulo", "first", Ct);

        DomainEvent published = await SingleAsync(bus, EventCatalogue.IdentityActivated);

        Assert.Contains("paulo", published.PayloadJson!, StringComparison.Ordinal);
    }

    // ---- self ----

    [Fact]
    public async Task TheOperationalStateIsAnnouncedOnATransitionAndNotOnEveryReading()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));
        SqliteEventBus bus = Bus(db, clock);
        var probe = new FakeResourceProbe();
        var resources = new SystemResourceModel(probe, clock);

        var audit = new SqliteAuditStore(
            db.Factory, clock, new byte[32],
            new AuditAnchorFile(Path.Combine(Path.GetTempPath(), $"a-{Guid.NewGuid():N}")));

        var self = new SqliteSelfModel(
            db.Factory, new StaticCapabilityRegistry([FakeClock()]), new FakePolicy(true),
            resources,
            new AuroraHealthService(
                db.Factory, audit, bus, resources, new AuditClockGuard(audit, clock),
                new SqliteScheduler(db.Factory, bus, new Adapters.Cognition.SqliteCognitiveCycle(db.Factory, clock), clock), PluginSandbox.ForThisMachine(),
                clock),
            new InMemoryIdempotencyStore(), clock, bus);

        await self.RefreshAsync("mind/local", Ct);
        await self.RefreshAsync("mind/local", Ct);
        await self.RefreshAsync("mind/local", Ct);

        // Three readings, one state: one event. Self refreshes on every capability check, and an
        // event per reading would make the bus a log of Aurora looking at itself.
        Assert.Single(
            await PublishedAsync(bus), e => e.Type == EventCatalogue.OperationalStateChanged);

        probe.Disk = 0.99;
        await self.RefreshAsync("mind/local", Ct);

        IReadOnlyList<DomainEvent> transitions =
            (await PublishedAsync(bus)).Where(e => e.Type == EventCatalogue.OperationalStateChanged).ToList();

        Assert.Equal(2, transitions.Count);
        Assert.Contains(OperationalState.Degraded, transitions[^1].PayloadJson!, StringComparison.Ordinal);
    }

    // ---- LAW-005: state that crosses a boundary says who owns it ----

    [Fact]
    public async Task EveryEventCarriesTheTenantThatOwnsWhatItDescribes()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));
        SqliteEventBus bus = Bus(db, clock);
        var planner = new SqlitePlanner(db.Factory, clock, bus);

        await planner.DraftAsync(new GoalRequest("t", "o", "paulo", [], []), Ct);

        await using Microsoft.Data.Sqlite.SqliteConnection connection = await db.Factory.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT tenant_id FROM domain_event;";

        var tenants = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            tenants.Add(reader.GetString(0));
        }

        // Present and constant, multi-tenancy is a data change. Absent, it is a redesign — and
        // orphan state is what LAW-005 says makes an agent system impossible to debug or erase.
        Assert.Equal([Tenant.Local], tenants);
    }

    [Fact]
    public async Task AnEventNamingAnotherTenantIsRefused()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));
        SqliteEventBus bus = Bus(db, clock);

        await Assert.ThrowsAsync<EventContractException>(() =>
            bus.PublishAsync(
                new OutboxWrite(
                    EventCatalogue.GoalDrafted, 1, EventCatalogue.Producers.Planner, "c-1",
                    Sensitivity.Private, TenantId: "tenant/somebody-else", PayloadJson: "{}"),
                Ct));
    }

    // ---- the whole platform, not only the bus ----

    [Fact]
    public void EveryDeclaredProducerHasSomethingItCanActuallyEmit()
    {
        // ADR 0027 called LAW-007 half-satisfied because producers existed on paper and published
        // nothing. A producer with no declared event is that same gap, back in the catalogue.
        var producers = typeof(EventCatalogue.Producers)
            .GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(producers);
        Assert.All(producers, producer => Assert.NotEmpty(EventCatalogue.For(producer)));
    }

    private static FakeCapability FakeClock() =>
        new(
            FakeCapability.LowReadOnly(
                "clock.now", """{"type":"object","additionalProperties":false}"""),
            _ => System.Text.Json.JsonDocument.Parse("{}").RootElement);
}
