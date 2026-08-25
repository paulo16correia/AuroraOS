using Aurora.Adapters.Knowledge;
using Aurora.Adapters.Events;
using Aurora.Adapters.Memories;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>Conformance tests for RFC 04.</summary>
public sealed class KnowledgeGraphTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static (SqliteKnowledgeGraph Graph, SqliteMemoryService Memories) New(
        SqliteTestDb db, DateTimeOffset? now = null)
    {
        var clock = new TestClock(now ?? DateTimeOffset.UnixEpoch);
        var memories = new SqliteMemoryService(db.Factory, new LexicalMemoryRanker(), TestBus.Over(db.Factory, clock), clock);
        return (new SqliteKnowledgeGraph(db.Factory, memories, clock), memories);
    }

    private static PredicateSchema Uses(bool acyclic = false) => new(
        "uses", "Uses", ["Project"], ["Technology"], Cardinality.Many, null, null, acyclic);

    private static async Task<MemoryRecord> RememberAsync(
        SqliteMemoryService memories, string predicate = "uses", string subject = "project/aurora",
        string obj = """{"tech":"sqlite"}""", string createdBy = MemoryOrigin.User,
        string sensitivity = Sensitivity.Private, string? validFrom = null, string? validTo = null) =>
        await memories.RecordAsync(
            new MemoryCandidate(
                MemoryKind.Semantic, subject, predicate, obj, $"{subject} {predicate}", 0.9, sensitivity,
                ValidFromUtc: validFrom, ValidToUtc: validTo),
            new MemoryProvenance(["doc/1"], ["page/2"], createdBy, "policy/owner",
                [new MemoryAnchor(MemoryAnchorKind.Document, "doc/1", "the fact was stated in this document")]), Ct);

    private static MemoryAccessContext Owner(string max = Sensitivity.Secret) =>
        new("owner", ["policy/owner"], max);

    // ---- rule 1: typed only ----

    [Fact]
    public async Task AnUnregisteredPredicateNeverEntersTheGraph()
    {
        using var db = new SqliteTestDb();
        var (graph, memories) = New(db);
        MemoryRecord memory = await RememberAsync(memories, predicate: "vibes_with");

        GraphChangeSet change = await graph.ProposeAsync(memory.Id, Ct);

        Assert.Empty(change.Relations);
        Assert.Contains(change.Rejections, r => r.Contains("not in the schema", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARegisteredPredicateProducesATypedEdge()
    {
        using var db = new SqliteTestDb();
        var (graph, memories) = New(db);
        await graph.RegisterPredicateAsync(Uses(), Ct);
        MemoryRecord memory = await RememberAsync(memories);

        GraphChangeSet change = await graph.ProposeAsync(memory.Id, Ct);

        KnowledgeRelation relation = Assert.Single(change.Relations);
        Assert.Equal("uses", relation.Predicate);
        Assert.Equal(RelationStatus.Asserted, relation.Status);
        Assert.Equal("Project", Assert.Single(change.Entities).Type);
    }

    [Fact]
    public async Task ARelationFromAnUnconfirmedMemoryStaysProposed()
    {
        using var db = new SqliteTestDb();
        var (graph, memories) = New(db);
        await graph.RegisterPredicateAsync(Uses(), Ct);
        MemoryRecord inferred = await RememberAsync(memories, createdBy: MemoryOrigin.System);

        GraphChangeSet change = await graph.ProposeAsync(inferred.Id, Ct);

        // PROPOSED relations are never the sole basis of an action.
        Assert.Equal(RelationStatus.Proposed, Assert.Single(change.Relations).Status);
    }

    // ---- rule 2: bounded expansion ----

    [Fact]
    public async Task ExpansionIsClampedToThreeHops()
    {
        using var db = new SqliteTestDb();
        var (graph, _) = New(db);
        KnowledgeEntity start = await graph.UpsertEntityAsync(
            new KnowledgeEntity("e1", "Project", "aurora", [], "{}", EntityStatus.Active,
                Sensitivity.Private, ["doc/1"]), Ct);

        Subgraph result = await graph.QueryAsync(
            new GraphPattern(StartEntityId: start.Id), depth: 99, Owner(), Ct);

        Assert.True(result.DepthReached <= SqliteKnowledgeGraph.MaxDepth);
    }

    // ---- rule 3: temporal validity ----

    [Fact]
    public async Task ANewAssertionClosesThePreviousOneWithoutDeletingIt()
    {
        using var db = new SqliteTestDb();

        // "Now" is 2025, so the window that closed in 2024 is history and the open one is current.
        var (graph, memories) = New(db, DateTimeOffset.Parse("2025-01-01T00:00:00+00:00"));
        await graph.RegisterPredicateAsync(
            new PredicateSchema("lives_in", "Lives in", ["Person"], ["City"], Cardinality.One, null, null), Ct);

        // A move, not a disagreement: the windows do not overlap, so neither memory disputes
        // the other and the graph records a succession.
        MemoryRecord first = await RememberAsync(
            memories, "lives_in", "person/paulo", """{"city":"Lisboa"}""",
            validFrom: "2020-01-01T00:00:00.0000000+00:00", validTo: "2024-01-01T00:00:00.0000000+00:00");
        await graph.ProposeAsync(first.Id, Ct);

        MemoryRecord second = await RememberAsync(
            memories, "lives_in", "person/paulo", """{"city":"Porto"}""",
            validFrom: "2024-01-01T00:00:00.0000000+00:00");
        await graph.ProposeAsync(second.Id, Ct);

        Subgraph now = await graph.QueryAsync(
            new GraphPattern(EntityType: "Person", SearchName: "person/paulo"), 1, Owner(), Ct);
        Subgraph history = await graph.QueryAsync(
            new GraphPattern(EntityType: "Person", SearchName: "person/paulo", AsOfNowOnly: false),
            1, Owner(), Ct);

        // "Is" answers with one; "was" still answers with both.
        Assert.Single(now.Relations);
        Assert.Equal(2, history.Relations.Count);
    }

    // ---- rule 4: reversible merge ----

    [Fact]
    public async Task MergeRedirectsAndCanBeReversed()
    {
        using var db = new SqliteTestDb();
        var (graph, _) = New(db);
        await graph.UpsertEntityAsync(new KnowledgeEntity(
            "a", "Person", "paulo", [], "{}", EntityStatus.Active, Sensitivity.Private, ["s"]), Ct);
        await graph.UpsertEntityAsync(new KnowledgeEntity(
            "b", "Person", "paulo c.", [], "{}", EntityStatus.Active, Sensitivity.Private, ["s"]), Ct);

        MergeRecord record = await graph.MergeAsync("a", "b", "owner", Ct);

        KnowledgeEntity merged = (await graph.GetEntityAsync("b", Ct))!;
        Assert.Equal(EntityStatus.Merged, merged.Status);
        Assert.Equal("a", merged.MergedIntoId);

        await graph.UnmergeAsync(record.Id, "owner", Ct);

        KnowledgeEntity restored = (await graph.GetEntityAsync("b", Ct))!;
        Assert.Equal(EntityStatus.Active, restored.Status);
        Assert.Null(restored.MergedIntoId);
    }

    [Fact]
    public async Task AMergeIsNotReversedTwice()
    {
        using var db = new SqliteTestDb();
        var (graph, _) = New(db);
        await graph.UpsertEntityAsync(new KnowledgeEntity(
            "a", "Person", "paulo", [], "{}", EntityStatus.Active, Sensitivity.Private, ["s"]), Ct);
        await graph.UpsertEntityAsync(new KnowledgeEntity(
            "b", "Person", "paulo c.", [], "{}", EntityStatus.Active, Sensitivity.Private, ["s"]), Ct);
        MergeRecord record = await graph.MergeAsync("a", "b", "owner", Ct);
        await graph.UnmergeAsync(record.Id, "owner", Ct);

        await Assert.ThrowsAsync<KnowledgeGraphException>(() => graph.UnmergeAsync(record.Id, "owner", Ct));
    }

    // ---- rule 5: SECRET produces no searchable node ----

    [Fact]
    public async Task ASecretEntityIsNotReachableByName()
    {
        using var db = new SqliteTestDb();
        var (graph, _) = New(db);
        await graph.UpsertEntityAsync(new KnowledgeEntity(
            "s1", "Account", "swiss-account", [], "{}", EntityStatus.Active, Sensitivity.Secret, ["s"]), Ct);

        Subgraph byName = await graph.QueryAsync(
            new GraphPattern(EntityType: "Account", SearchName: "swiss-account"), 1, Owner(), Ct);

        Assert.Empty(byName.Entities);

        // Still reachable by an id the caller already holds — the rule is about discoverability.
        Subgraph byId = await graph.QueryAsync(new GraphPattern(StartEntityId: "s1"), 1, Owner(), Ct);
        Assert.Single(byId.Entities);
    }

    [Fact]
    public async Task AnEntityAboveTheCallersCeilingIsNotReturned()
    {
        using var db = new SqliteTestDb();
        var (graph, _) = New(db);
        await graph.UpsertEntityAsync(new KnowledgeEntity(
            "c1", "Account", "shared", [], "{}", EntityStatus.Active, Sensitivity.Confidential, ["s"]), Ct);

        Subgraph result = await graph.QueryAsync(
            new GraphPattern(StartEntityId: "c1"), 1, Owner(max: Sensitivity.Private), Ct);

        Assert.Empty(result.Entities);
    }

    // ---- limit cases ----

    [Fact]
    public async Task HomonymsAreKeptSeparateAndReported()
    {
        using var db = new SqliteTestDb();
        var (graph, memories) = New(db);
        await graph.RegisterPredicateAsync(Uses(), Ct);

        // Two distinct entities already share a name, as a deliberate homonym split.
        await graph.UpsertEntityAsync(new KnowledgeEntity(
            "p1", "Project", "project/aurora", [], "{}", EntityStatus.Active, Sensitivity.Private, ["s"]), Ct);
        await graph.UpsertEntityAsync(new KnowledgeEntity(
            "p2", "Project", "project/aurora", [], "{}", EntityStatus.Active, Sensitivity.Private, ["s"]), Ct);

        GraphChangeSet change = await graph.ProposeAsync((await RememberAsync(memories)).Id, Ct);

        // The graph does not pick one; it creates a separate entity and says the name was ambiguous.
        Assert.Contains("project/aurora", change.AmbiguousNames);
        Assert.DoesNotContain(change.Entities, e => e.Id is "p1" or "p2");
    }

    [Fact]
    public async Task ACycleIsRejectedWithItsChain()
    {
        using var db = new SqliteTestDb();
        var (graph, _) = New(db);
        await graph.RegisterPredicateAsync(new PredicateSchema(
            "depends_on", "Depends on", ["Task"], ["Task"], Cardinality.Many, null, null, Acyclic: true), Ct);

        foreach (var id in new[] { "t1", "t2", "t3" })
        {
            await graph.UpsertEntityAsync(new KnowledgeEntity(
                id, "Task", id, [], "{}", EntityStatus.Active, Sensitivity.Private, ["s"]), Ct);
        }

        await graph.AssertRelationAsync("t1", "depends_on", "t2", ["m1"], Ct);
        await graph.AssertRelationAsync("t2", "depends_on", "t3", ["m2"], Ct);

        KnowledgeGraphException error = await Assert.ThrowsAsync<KnowledgeGraphException>(
            () => graph.AssertRelationAsync("t3", "depends_on", "t1", ["m3"], Ct));

        // The chain travels with the refusal, because "there is a cycle" is not actionable.
        Assert.Equal(["t3", "t1"], error.Cycle.Take(2));
        Assert.Contains("t1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEdgeWhoseTypesTheSchemaForbidsIsRejected()
    {
        using var db = new SqliteTestDb();
        var (graph, _) = New(db);
        await graph.RegisterPredicateAsync(Uses(), Ct);
        await graph.UpsertEntityAsync(new KnowledgeEntity(
            "p", "Project", "aurora", [], "{}", EntityStatus.Active, Sensitivity.Private, ["s"]), Ct);
        await graph.UpsertEntityAsync(new KnowledgeEntity(
            "q", "Person", "paulo", [], "{}", EntityStatus.Active, Sensitivity.Private, ["s"]), Ct);

        // uses(Project, Technology): a Person is not an allowed object.
        await Assert.ThrowsAsync<KnowledgeGraphException>(
            () => graph.AssertRelationAsync("p", "uses", "q", ["m1"], Ct));
    }

    [Fact]
    public async Task AWithdrawnSourceLeavesTheEdgeButRemovesAssertion()
    {
        using var db = new SqliteTestDb();
        var (graph, memories) = New(db);
        await graph.RegisterPredicateAsync(Uses(), Ct);
        MemoryRecord memory = await RememberAsync(memories);
        GraphChangeSet change = await graph.ProposeAsync(memory.Id, Ct);
        var relationId = change.Relations[0].Id;

        await memories.ForgetAsync(memory.Id, MemoryOrigin.User, Ct);
        var affected = await graph.OnSourceWithdrawnAsync(memory.Id, Ct);

        Assert.Equal(1, affected);

        RelationProvenance provenance = Assert.Single(await graph.ExplainAsync(relationId, Ct));
        Assert.Equal(RelationStatus.Proposed, provenance.Status);
        Assert.Equal([memory.Id], provenance.SourceMemoryIds);
    }

    [Fact]
    public async Task ExplainReturnsTheSourcesBehindARelation()
    {
        using var db = new SqliteTestDb();
        var (graph, memories) = New(db);
        await graph.RegisterPredicateAsync(Uses(), Ct);
        MemoryRecord memory = await RememberAsync(memories);
        GraphChangeSet change = await graph.ProposeAsync(memory.Id, Ct);

        RelationProvenance provenance = Assert.Single(
            await graph.ExplainAsync(change.Relations[0].Id, Ct));

        Assert.Equal([memory.Id], provenance.SourceMemoryIds);
    }
}
