using System.Globalization;
using Aurora.Adapters.Knowledge;
using Aurora.Adapters.Memories;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Relationships;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Relationships and preferences (RFC 029).
/// </summary>
/// <remarks>
/// The subject is a separation: a relationship is a fact about the world, a preference is a habit
/// of the person, and neither is a permission. Most of these tests are about that not blurring.
/// </remarks>
public sealed class RelationshipTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static (SqliteRelationshipModel Model, SqliteKnowledgeGraph Graph, TestClock Clock) Build(
        SqliteTestDb db, string now = "2026-01-15T09:00:00+00:00")
    {
        var clock = new TestClock(At(now));
        var graph = new SqliteKnowledgeGraph(
            db.Factory,
            new SqliteMemoryService(
                db.Factory, new LexicalMemoryRanker(), TestBus.Over(db.Factory, clock), clock),
            clock);

        return (new SqliteRelationshipModel(db.Factory, graph, clock), graph, clock);
    }

    private static RelationshipCandidate Employs(
        string subject = "person/owner", string obj = "org/acme") =>
        new(subject, RelationType.Professional, obj, 0.9);

    // ---- rule 1: a tie is not a permission ----

    [Fact]
    public async Task ARelationshipDefaultsToNoAuthorityAtAll()
    {
        using var db = new SqliteTestDb();
        var (model, _, _) = Build(db);

        RelationshipAssertion tie = await model.AssertAsync(Employs(), ["contract/1"], Ct);

        // "You are a client" is a fact about a tie and says nothing about acting for anyone.
        Assert.Equal(AuthorityScope.None, tie.AuthorityScope);
    }

    [Theory]
    [InlineData(AuthorityScope.Correspond)]
    [InlineData(AuthorityScope.ActOnBehalf)]
    public async Task ClaimingAuthorityNeedsItsOwnApprovalAndNotJustEvidenceOfTheTie(string scope)
    {
        using var db = new SqliteTestDb();
        var (model, _, _) = Build(db);

        // The evidence shows the relationship. It does not show that anybody agreed to Aurora
        // acting on it, which is a different thing that needs its own grant.
        RelationshipException refused = await Assert.ThrowsAsync<RelationshipException>(() =>
            model.AssertAsync(Employs() with { AuthorityScope = scope }, ["contract/1"], Ct));

        Assert.Contains("own approval", refused.Message, StringComparison.Ordinal);

        RelationshipAssertion granted = await model.AssertAsync(
            Employs() with { AuthorityScope = scope, AuthorizationRef = "approval/7" },
            ["contract/1"], Ct);

        Assert.Equal(scope, granted.AuthorityScope);
    }

    [Fact]
    public void NothingOnTheInterfaceTurnsARelationshipIntoAPermission()
    {
        // Rule 1 holds by there being no method that crosses between them: the way to keep
        // relationship, permission and identity separate is to have no bridge.
        var mentioned = typeof(IRelationshipModel).GetMethods()
            .SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType))
            .SelectMany(t => t.IsGenericType ? t.GetGenericArguments() : [t])
            .ToList();

        Assert.DoesNotContain(typeof(Principal), mentioned);
        Assert.DoesNotContain(typeof(CapabilityDescriptor), mentioned);
        Assert.DoesNotContain(typeof(ApprovalEvaluation), mentioned);
    }

    // ---- limit case: two people with the same name ----

    [Fact]
    public async Task ATieAgainstAnUnresolvedEntityIsRefused()
    {
        using var db = new SqliteTestDb();
        var (model, _, _) = Build(db);

        // Until resolution has settled which person is meant, asserting a tie attaches a fact to a
        // guess — and a wrong relationship is worse than a missing one, because it looks like
        // knowledge.
        await Assert.ThrowsAsync<RelationshipException>(() =>
            model.AssertAsync(
                Employs(obj: "entity/who-knows"), ["conversation/1"], Ct));
    }

    [Fact]
    public async Task ATieAgainstAResolvedEntityIsAccepted()
    {
        using var db = new SqliteTestDb();
        var (model, graph, _) = Build(db);

        KnowledgeEntity acme = await graph.UpsertEntityAsync(
            new KnowledgeEntity(
                "acme", "ORGANISATION", "Acme Ltd", [], "{}", "ACTIVE",
                Sensitivity.Public, ["contract/1"]),
            Ct);

        RelationshipAssertion tie = await model.AssertAsync(
            Employs(obj: $"entity/{acme.Id}"), ["contract/1"], Ct);

        Assert.Equal(RelationshipStatus.Active, tie.Status);
    }

    // ---- rule 3: someone else's relationships are someone else's ----

    [Fact]
    public async Task AThirdPartySRelationshipNeedsAnAuthorisationAndGetsARetention()
    {
        using var db = new SqliteTestDb();
        var (model, _, _) = Build(db);

        await Assert.ThrowsAsync<RelationshipException>(() =>
            model.AssertAsync(Employs(subject: "person/someone-else"), ["hearsay/1"], Ct));

        RelationshipAssertion stored = await model.AssertAsync(
            Employs(subject: "person/someone-else") with { AuthorizationRef = "approval/3" },
            ["contract/2"], Ct);

        Assert.NotNull(stored.RetentionUntilUtc);
    }

    [Fact]
    public async Task AThirdPartySRelationshipStopsBeingInForceAtItsRetention()
    {
        using var db = new SqliteTestDb();
        var (model, _, clock) = Build(db);

        RelationshipAssertion stored = await model.AssertAsync(
            Employs(subject: "person/someone-else") with
            {
                AuthorizationRef = "approval/3", Retention = TimeSpan.FromDays(30),
            },
            ["contract/2"], Ct);

        clock.UtcNow = At("2026-03-01T09:00:00+00:00");
        await model.ReviewDueAsync(Ct);

        Assert.Empty(await model.InForceAsync("person/someone-else", clock.UtcNow, Ct));

        // The row stays: rule 4 keeps the history, and rule 3 only bounds how long it is acted on.
        Assert.Single(await model.HistoryAsync("person/someone-else", Ct));
    }

    // ---- rule 4: the past is not rewritten ----

    [Fact]
    public async Task EndingARelationshipClosesItsIntervalAndKeepsIt()
    {
        using var db = new SqliteTestDb();
        var (model, _, clock) = Build(db);

        RelationshipAssertion tie = await model.AssertAsync(Employs(), ["contract/1"], Ct);

        clock.UtcNow = At("2026-06-01T09:00:00+00:00");
        RelationshipAssertion ended = await model.EndAsync(tie.Id, "termination/1", Ct);

        Assert.Equal(RelationshipStatus.Ended, ended.Status);
        Assert.NotNull(ended.ValidToUtc);

        // Not in force now, and it was in force in March. Both have to remain true.
        Assert.Empty(await model.InForceAsync("person/owner", clock.UtcNow, Ct));
        Assert.Single(await model.InForceAsync("person/owner", At("2026-03-01T09:00:00+00:00"), Ct));
    }

    [Fact]
    public async Task ReassigningARelationshipLeavesTheOldOneStandingInThePast()
    {
        using var db = new SqliteTestDb();
        var (model, _, clock) = Build(db);

        RelationshipAssertion first = await model.AssertAsync(Employs(), ["contract/1"], Ct);

        clock.UtcNow = At("2026-06-01T09:00:00+00:00");
        await model.EndAsync(first.Id, "termination/1", Ct);
        await model.AssertAsync(Employs(obj: "org/other"), ["contract/2"], Ct);

        IReadOnlyList<RelationshipAssertion> history = await model.HistoryAsync("person/owner", Ct);

        Assert.Equal(2, history.Count);
        Assert.Contains(history, r => r.ObjectRef == "org/acme" && r.ValidToUtc is not null);
        Assert.Contains(history, r => r.ObjectRef == "org/other" && r.ValidToUtc is null);
    }

    [Fact]
    public async Task ADisputedRelationshipStopsBeingInForceAndStaysOnRecord()
    {
        using var db = new SqliteTestDb();
        var (model, _, clock) = Build(db);

        RelationshipAssertion tie = await model.AssertAsync(Employs(), ["contract/1"], Ct);
        await model.DisputeAsync(tie.Id, "email/9", "they say the contract never started", Ct);

        Assert.Empty(await model.InForceAsync("person/owner", clock.UtcNow, Ct));
        Assert.Single(await model.HistoryAsync("person/owner", Ct));
    }

    // ---- rule 2: a habit does not license an act ----

    [Fact]
    public async Task WhatThePersonSaidDisplacesWhatAuroraWorkedOut()
    {
        using var db = new SqliteTestDb();
        var (model, _, _) = Build(db);

        Preference inferred = await model.InferAsync(
            new Preference(
                "", "person/owner", "person/owner", PreferenceDimension.Tone,
                """{"tone":"formal"}""", 0.7, PreferenceBasis.Observed, [], "{}",
                PreferenceStatus.Active, "", true),
            ["conversation/3"], Ct);

        Assert.Equal(PreferenceStatus.Active, inferred.Status);

        await model.SetExplicitAsync(
            "person/owner", "person/owner", PreferenceDimension.Tone,
            """{"tone":"blunt"}""", ["conversation/9"], Ct);

        PreferenceResolution resolved = await model.ResolveAsync(
            "person/owner", PreferenceDimension.Tone, PreferenceEffect.Presentational, Ct);

        // Rejected rather than deleted: what Aurora guessed, and that the person corrected it, is
        // worth being able to read later.
        Preference active = Assert.Single(resolved.Preferences);
        Assert.Equal(PreferenceBasis.Explicit, active.Basis);
        Assert.Contains("blunt", active.ValueJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInferenceNeverDisplacesWhatThePersonSaid()
    {
        using var db = new SqliteTestDb();
        var (model, _, _) = Build(db);

        await model.SetExplicitAsync(
            "person/owner", "person/owner", PreferenceDimension.Tone,
            """{"tone":"blunt"}""", ["conversation/9"], Ct);

        Preference inferred = await model.InferAsync(
            new Preference(
                "", "person/owner", "person/owner", PreferenceDimension.Tone,
                """{"tone":"formal"}""", 0.9, PreferenceBasis.Inferred, [], "{}",
                PreferenceStatus.Active, "", true),
            ["conversation/12"], Ct);

        // The person's own words outrank a pattern about them, in that direction and not the other.
        Assert.Equal(PreferenceStatus.Rejected, inferred.Status);
    }

    [Theory]
    [InlineData(PreferenceEffect.Purchase)]
    [InlineData(PreferenceEffect.ExternalCommunication)]
    [InlineData(PreferenceEffect.SensitiveData)]
    [InlineData(PreferenceEffect.PersistentChange)]
    public async Task AnInferredPreferenceNeverActsUnaskedOnAnythingThatMatters(string effect)
    {
        using var db = new SqliteTestDb();
        var (model, _, _) = Build(db);

        await model.InferAsync(
            new Preference(
                "", "person/owner", "person/owner", PreferenceDimension.Tool,
                """{"tool":"the usual one"}""", 0.95, PreferenceBasis.Observed, [], "{}",
                PreferenceStatus.Active, "", true),
            ["order/1", "order/2", "order/3"], Ct);

        PreferenceResolution resolved = await model.ResolveAsync(
            "person/owner", PreferenceDimension.Tool, effect, Ct);

        Assert.False(resolved.MayActWithoutConfirmation);
        Assert.Contains("confirmation", resolved.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInferredPreferenceMayShapeHowSomethingIsPresented()
    {
        using var db = new SqliteTestDb();
        var (model, _, _) = Build(db);

        await model.InferAsync(
            new Preference(
                "", "person/owner", "person/owner", PreferenceDimension.Format,
                """{"format":"bullets"}""", 0.8, PreferenceBasis.Observed, [], "{}",
                PreferenceStatus.Active, "", true),
            ["conversation/3"], Ct);

        PreferenceResolution resolved = await model.ResolveAsync(
            "person/owner", PreferenceDimension.Format, PreferenceEffect.Presentational, Ct);

        // Getting a format wrong costs a sentence; getting a purchase wrong costs money. The line
        // is drawn where the cost changes kind.
        Assert.True(resolved.MayActWithoutConfirmation);
    }

    [Fact]
    public async Task WhatThePersonSaidMayActWithoutAskingAgain()
    {
        using var db = new SqliteTestDb();
        var (model, _, _) = Build(db);

        await model.SetExplicitAsync(
            "person/owner", "person/owner", PreferenceDimension.Time,
            """{"never_before":"09:00"}""", ["conversation/4"], Ct);

        PreferenceResolution resolved = await model.ResolveAsync(
            "person/owner", PreferenceDimension.Time, PreferenceEffect.ExternalCommunication, Ct);

        Assert.True(resolved.MayActWithoutConfirmation);
    }

    [Fact]
    public async Task AnInferredPreferenceNobodyConfirmsExpiresAndAnExplicitOneDoesNot()
    {
        using var db = new SqliteTestDb();
        var (model, _, clock) = Build(db);

        await model.InferAsync(
            new Preference(
                "", "person/owner", "person/owner", PreferenceDimension.Tool,
                """{"tool":"x"}""", 0.8, PreferenceBasis.Observed, [], "{}",
                PreferenceStatus.Active, "", true),
            ["order/1"], Ct);

        await model.SetExplicitAsync(
            "person/owner", "person/owner", PreferenceDimension.Time,
            """{"never_before":"09:00"}""", ["conversation/4"], Ct);

        clock.UtcNow = At("2026-06-01T09:00:00+00:00");
        await model.ReviewDueAsync(Ct);

        Assert.Empty(
            (await model.ResolveAsync(
                "person/owner", PreferenceDimension.Tool, PreferenceEffect.Presentational, Ct)).Preferences);

        // What somebody said stands until they say otherwise.
        Assert.Single(
            (await model.ResolveAsync(
                "person/owner", PreferenceDimension.Time, PreferenceEffect.Presentational, Ct)).Preferences);
    }
}
