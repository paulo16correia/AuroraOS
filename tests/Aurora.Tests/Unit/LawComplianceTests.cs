using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Aurora.Adapters.Capabilities;
using Aurora.Adapters.Cognition;
using Aurora.Adapters.Genomes;
using Aurora.Adapters.Operations;
using Aurora.Adapters.Plugins.Sandboxes;
using Aurora.Adapters.Resources;
using Aurora.Adapters.Scheduling;
using Aurora.Adapters.Self;
using Aurora.Adapters.Events;
using Aurora.Adapters.Knowledge;
using Aurora.Adapters.Memories;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Vault;
using Aurora.Adapters.World;
using Aurora.Core.Abstractions;
using Microsoft.Data.Sqlite;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Compliance tests for LAW-001 to LAW-008.
/// </summary>
/// <remarks>
/// These are the tests the v1.0 architecture review names as a mandatory condition before any
/// capability with external effects is offered. Each one is titled after the law's own verifiable
/// control rather than after the code it exercises, so a reader can check the law against the test
/// without knowing the implementation.
/// </remarks>
public sealed class LawComplianceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly Principal Caller = new("c1", "u1");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");

    private static SqliteMemoryService Memories(SqliteTestDb db)
    {
        var clock = new TestClock(Now);
        return new SqliteMemoryService(
            db.Factory, new LexicalMemoryRanker(), TestBus.Over(db.Factory, clock), clock);
    }

    private static SqliteWorldModel World(SqliteTestDb db, DateTimeOffset? now = null) =>
        new(db.Factory, new TestClock(now ?? Now), WorldModelOptions.Default);

    private static MemoryCandidate Candidate() => new(
        MemoryKind.Semantic, "person/paulo", "prefers", """{"drink":"tea"}""",
        "Paulo prefers tea", 0.8, Sensitivity.Private);

    private static IReadOnlyList<MemoryAnchor> Anchor() =>
        [new MemoryAnchor(MemoryAnchorKind.Conversation, "conversation/1", "stated in this conversation")];

    private static readonly MemoryAccessContext Access = new("owner", ["policy/owner"], Sensitivity.Private);

    private static SqliteAuditStore TestAudit(SqliteTestDb db, TestClock clock) => new(
        db.Factory, clock, new byte[32],
        new AuditAnchorFile(Path.Combine(Path.GetTempPath(), $"law008-{Guid.NewGuid():N}")));

    /// <summary>A real Self over a real audit store, because the trace is what is being asserted.</summary>
    private static SqliteSelfModel SelfOver(SqliteTestDb db, TestClock clock, IAuditStore audit)
    {
        var bus = TestBus.Over(db.Factory, clock);
        var resources = new SystemResourceModel(new FakeResourceProbe(), clock);

        var health = new AuroraHealthService(
            db.Factory, audit, bus, resources, new AuditClockGuard(audit, clock),
            new SqliteScheduler(db.Factory, bus, new SqliteCognitiveCycle(db.Factory, clock), clock),
            PluginSandbox.ForThisMachine(), clock);

        return new SqliteSelfModel(
            db.Factory, new StaticCapabilityRegistry([]), new FakePolicy(true), resources, health,
            new InMemoryIdempotencyStore(), audit, clock, bus);
    }

    // ================= LAW-001 — Nothing enters Mind directly =================
    // Control: tests must reject a Memory, Belief, Preference or WorldAssertion without provenance.

    [Fact]
    public async Task Law001_AMemoryWithoutProvenanceIsRejected()
    {
        using var db = new SqliteTestDb();

        await Assert.ThrowsAsync<MemoryException>(() => Memories(db).RecordAsync(
            Candidate(),
            new MemoryProvenance([], ["e"], MemoryOrigin.User, "policy/owner", Anchor()), Ct));
    }

    [Fact]
    public async Task Law001_AWorldAssertionWithoutEvidenceIsRejected()
    {
        using var db = new SqliteTestDb();
        var world = World(db);
        WorldModelVersion version = await world.BeginVersionAsync("mind-1", null, Ct);
        await world.ActivateVersionAsync(version.Id, "owner", Ct);

        await Assert.ThrowsAsync<WorldModelException>(() => world.ObserveAsync(
            new WorldObservation(
                "s", "p", WorldPredicateCategory.Social, "o", null,
                EvidenceRefs: [], 1, Now.ToString("O"), Now.ToString("O")),
            version.Id, Ct));
    }

    [Fact]
    public async Task Law001_AToolOutputCannotBecomeCanonicalStateOnItsOwn()
    {
        using var db = new SqliteTestDb();
        var world = World(db);
        WorldModelVersion version = await world.BeginVersionAsync("mind-1", null, Ct);
        await world.ActivateVersionAsync(version.Id, "owner", Ct);

        WorldAssertion observed = await world.ObserveAsync(
            new WorldObservation(
                "person/paulo", "works_in", WorldPredicateCategory.Social, "project/aurora", null,
                ["tool/scrape-1"], 0.9, Now.ToString("O"), Now.ToString("O")),
            version.Id, Ct);

        // It lands as a proposal, and the tool cannot promote its own observation.
        Assert.Equal(WorldAssertionStatus.Proposed, observed.Status);
        await Assert.ThrowsAsync<WorldModelException>(
            () => world.ValidateAsync(observed.Id, SqliteWorldModel.ToolActor, [], Ct));
    }

    [Fact]
    public async Task Law001_AnUnconfirmedMemoryProducesOnlyAProposedRelation()
    {
        using var db = new SqliteTestDb();
        var memories = Memories(db);
        var graph = new SqliteKnowledgeGraph(db.Factory, memories, new TestClock(Now));
        await graph.RegisterPredicateAsync(
            new PredicateSchema("prefers", "Prefers", ["Person"], ["Thing"], Cardinality.Many, null, null), Ct);

        MemoryRecord inferred = await memories.RecordAsync(
            Candidate(),
            new MemoryProvenance(["model/1"], ["turn/2"], MemoryOrigin.System, "policy/owner", Anchor()), Ct);

        GraphChangeSet change = await graph.ProposeAsync(inferred.Id, Ct);

        Assert.Equal(RelationStatus.Proposed, Assert.Single(change.Relations).Status);
    }

    // ================= LAW-002 — Mind never communicates directly with tools =================
    // Control: MindService does not expose network, shell, email or credential methods.

    [Fact]
    public void Law002_NoMindLayerInterfaceTouchesAToolOrACredential()
    {
        Type[] mindLayer =
        [
            typeof(IMemoryService), typeof(IKnowledgeGraph), typeof(IWorldModel),
            typeof(IDecisionEngine), typeof(IPlanner), typeof(IAttentionSystem),
            typeof(IWorkingMemory), typeof(IMindStateService),
        ];

        Type[] forbidden =
        [
            typeof(ToolCall), typeof(ToolManifest), typeof(ToolResult),
            typeof(IToolConnector), typeof(IToolManager),
            typeof(EphemeralSecretHandle), typeof(IVault),
        ];

        var violations = new List<string>();

        foreach (Type contract in mindLayer)
        {
            foreach (MethodInfo method in contract.GetMethods())
            {
                IEnumerable<Type> touched = method.GetParameters()
                    .Select(p => p.ParameterType)
                    .Append(method.ReturnType)
                    .SelectMany(Unwrap);

                foreach (Type type in touched.Where(t => forbidden.Contains(t)))
                {
                    violations.Add($"{contract.Name}.{method.Name} touches {type.Name}");
                }
            }
        }

        // The Mind represents state; it does not reach for a connector or a credential. If this
        // ever fails, the boundary moved rather than the test being wrong.
        Assert.Empty(violations);

        static IEnumerable<Type> Unwrap(Type type) =>
            type.IsGenericType ? type.GetGenericArguments().Append(type) : [type];
    }

    [Fact]
    public async Task Law002_EveryToolCallCarriesItsPolicyDecisionAndApproval()
    {
        using var db = new SqliteTestDb();
        var manager = ToolManagerTestsSupport.Manager(db, out _);
        await manager.RegisterAsync(ToolManagerTestsSupport.Connector(requiresApproval: true), Ct);

        ToolCall proposed = await manager.ProposeAsync(
            "work/1", null, "mailer", "communication.send", """{"body":"hi"}""", "k1", Ct);

        // Neither half may be assumed: no policy decision, and no approval where one is required.
        await Assert.ThrowsAsync<ToolException>(() => manager.AuthorizeAsync(proposed.Id, [], "a1", Ct));
        await Assert.ThrowsAsync<ToolException>(
            () => manager.AuthorizeAsync(proposed.Id, ["policy/1"], null, Ct));

        ToolCall authorized = await manager.AuthorizeAsync(proposed.Id, ["policy/1"], "approval/1", Ct);

        Assert.Equal(["policy/1"], authorized.PolicyDecisionIds);
        Assert.Equal("approval/1", authorized.ApprovalId);
    }

    // ================= LAW-003 — Every action generates an observation =================

    [Fact]
    public async Task Law003_AnExecutedCycleCannotCloseWithoutAnObservation()
    {
        using var db = new SqliteTestDb();
        var cycle = new SqliteCognitiveCycle(db.Factory, new TestClock(Now));
        CognitiveCycle started = await cycle.RunAsync(new CycleIngress("work/1", "mcp/1", null), Ct);

        foreach (var stage in CycleStage.Order.TakeWhile(s => s != CycleStage.Observation))
        {
            await cycle.AdvanceAsync(started.Id, stage, [], [], null, Ct);
        }

        await cycle.MarkExecutedAsync(started.Id, true, true, Ct);

        await Assert.ThrowsAsync<CognitiveCycleException>(
            () => cycle.CompleteAsync(started.Id, true, "did the thing", Ct));
    }

    [Fact]
    public async Task Law003_ATimeoutProducesUnknownRatherThanPresumedSuccess()
    {
        using var db = new SqliteTestDb();
        var manager = ToolManagerTestsSupport.Manager(db, out _);
        await manager.RegisterAsync(ToolManagerTestsSupport.TimingOutConnector(), Ct);

        ToolCall proposed = await manager.ProposeAsync(
            "work/1", null, "mailer", "communication.send", """{"body":"hi"}""", "k1", Ct);
        ToolCall authorized = await manager.AuthorizeAsync(proposed.Id, ["policy/1"], null, Ct);
        ToolCall executed = await manager.ExecuteAsync(authorized.Id, Ct);

        Assert.Equal(ToolCallStatus.Unknown, executed.Status);

        // And it is visible as pending reconciliation, not quietly forgotten.
        Assert.Single(await manager.UnknownCallsAsync(Ct));
    }

    // ================= LAW-004 — No memory is born in isolation =================

    [Fact]
    public async Task Law004_AMemoryWithoutAnAnchorIsRejected()
    {
        using var db = new SqliteTestDb();

        await Assert.ThrowsAsync<MemoryException>(() => Memories(db).RecordAsync(
            Candidate(),
            new MemoryProvenance(["conversation/1"], ["turn/2"], MemoryOrigin.User, "policy/owner", []), Ct));
    }

    [Fact]
    public async Task Law004_AnAnchorWithoutAReasonIsRejected()
    {
        using var db = new SqliteTestDb();

        // The control is explicit that links hold reason and evidence: a bare pointer is not a
        // relation anyone can later explain.
        await Assert.ThrowsAsync<MemoryException>(() => Memories(db).RecordAsync(
            Candidate(),
            new MemoryProvenance(
                ["conversation/1"], ["turn/2"], MemoryOrigin.User, "policy/owner",
                [new MemoryAnchor(MemoryAnchorKind.Entity, "person/paulo", "   ")]), Ct));
    }

    [Fact]
    public async Task Law004_AnUnknownAnchorKindIsRejected()
    {
        using var db = new SqliteTestDb();

        await Assert.ThrowsAsync<MemoryException>(() => Memories(db).RecordAsync(
            Candidate(),
            new MemoryProvenance(
                ["conversation/1"], ["turn/2"], MemoryOrigin.User, "policy/owner",
                [new MemoryAnchor("VIBES", "somewhere", "it felt related")]), Ct));
    }

    [Fact]
    public async Task Law004_AnchorsAreKeptOnTheRecordForLaterAudit()
    {
        using var db = new SqliteTestDb();
        var memories = Memories(db);

        MemoryRecord recorded = await memories.RecordAsync(
            Candidate(),
            new MemoryProvenance(
                ["conversation/1"], ["turn/2"], MemoryOrigin.User, "policy/owner",
                [new MemoryAnchor(MemoryAnchorKind.Goal, "goal/7", "raised while planning the report")]), Ct);

        MemoryRecord reloaded = (await memories.GetAsync(recorded.Id, Ct))!;

        MemoryAnchor anchor = Assert.Single(reloaded.Anchors);
        Assert.Equal(MemoryAnchorKind.Goal, anchor.Kind);
        Assert.Contains("planning", anchor.Reason, StringComparison.Ordinal);
    }

    // ================= LAW-005 — Every state has an owner, life cycle and border =================
    // Control: temporary objects expire; nothing lives without an owner and a retention agreement.

    [Fact]
    public async Task Law005_EveryTemporaryConstructExpires()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(Now);
        var policy = AttentionPolicy.Default;

        // Working memory.
        var attention = new SqliteAttentionSystem(db.Factory, new SensitivityAttentionAuthorization(), clock);
        AttentionSet set = await attention.RankAsync(
            "cycle-1", [], policy, new MemoryAccessContext("owner", ["policy/owner"], Sensitivity.Secret), Ct);
        var working = new SqliteWorkingMemory(db.Factory, clock, WorkingMemoryOptions.Default);
        await working.OpenAsync("cycle-1", null, set, policy, Ct);

        // Consent session.
        var sessions = new SqliteConsentSessionStore(
            db.Factory, clock, new FakeServerIdentity("boot-1"),
            new VersionedFakePolicy(true, "pv-1"), ConsentSessionOptions.Default);
        await sessions.OpenAsync(Caller, Ct);

        // Decision.
        var decisions = new SqliteDecisionEngine(db.Factory, clock);
        await decisions.EvaluateAsync(
            new DecisionThought("cycle-1", null, [Respond()], ["evidence/1"], 0.5, "LOW"),
            new DecisionContext(true, [SilenceReason.NoiseLimit], Now.AddMinutes(5).ToString("O")), Ct);

        var later = new TestClock(Now.AddDays(1));

        Assert.Equal(1, await new SqliteWorkingMemory(db.Factory, later, WorkingMemoryOptions.Default)
            .ExpireDueAsync(Ct));
        Assert.Equal(0, await new SqliteConsentSessionStore(
            db.Factory, later, new FakeServerIdentity("boot-1"),
            new VersionedFakePolicy(true, "pv-1"), ConsentSessionOptions.Default).CountActiveAsync(Ct));
        Assert.Equal(1, await new SqliteDecisionEngine(db.Factory, later).ExpireDueAsync(Ct));

        static DecisionOption Respond() => new(
            DecisionMode.Respond, "answer", [],
            new OptionEvaluation(0.9, true, "LOW", 1, true, true), [], []);
    }

    [Fact]
    public async Task Law005_PersistentStateNamesItsOwner()
    {
        using var db = new SqliteTestDb();
        var memories = Memories(db);

        MemoryRecord memory = await memories.RecordAsync(
            Candidate(),
            new MemoryProvenance(["conversation/1"], ["turn/2"], MemoryOrigin.User, "policy/owner", Anchor()), Ct);

        // Who may read it and who created it are both on the record, not implied by where it sits.
        Assert.Equal("policy/owner", memory.AccessPolicyId);
        Assert.Equal(MemoryOrigin.User, memory.CreatedBy);
        Assert.False(string.IsNullOrWhiteSpace(memory.SensitivityClass));
    }

    // ================= LAW-006 — There is no silent autonomy =================
    // Control: automations require scope, validity, cost limit and pause switch.

    [Fact]
    public async Task Law006_AnAutomationHasScopeValidityCeilingAndAPauseSwitch()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(Now);
        var sessions = new SqliteConsentSessionStore(
            db.Factory, clock, new FakeServerIdentity("boot-1"),
            new VersionedFakePolicy(true, "pv-1"), new ConsentSessionOptions(TimeSpan.FromMinutes(15), 2));

        ConsentSession session = await sessions.OpenAsync(Caller, Ct);

        // Validity and ceiling.
        Assert.NotEqual(session.CreatedAtUtc, session.ExpiresAtUtc);
        Assert.Equal(2, session.MaxActions);

        await sessions.TryUseAsync(Caller, Ct);
        await sessions.TryUseAsync(Caller, Ct);
        Assert.Equal(ConsentSessionUseOutcome.None, (await sessions.TryUseAsync(Caller, Ct)).Outcome);

        // Pause switch.
        await sessions.OpenAsync(Caller, Ct);
        Assert.True(await sessions.RevokeAllAsync(Ct) > 0);
        Assert.Equal(0, await sessions.CountActiveAsync(Ct));
    }

    [Fact]
    public async Task Law006_ScopeIsReadOnly_SoAWriteIsNeverCoveredSilently()
    {
        using var db = new SqliteTestDb();
        var approvals = new FakeApprovalStore();
        var sessions = new SqliteConsentSessionStore(
            db.Factory, new TestClock(Now), new FakeServerIdentity("boot-1"),
            new VersionedFakePolicy(true, "pv-1"), ConsentSessionOptions.Default);
        var gate = new Aurora.Adapters.Consent.SessionAwareConsentGate(approvals, sessions);

        await sessions.OpenAsync(Caller, Ct);

        CapabilityDescriptor write = new(
            "files.write", "write", "test",
            System.Text.Json.JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone(),
            ["files.write"], RiskLevel.Medium, ApprovalRequired: true);

        ConsentOutcome outcome = await gate.EvaluateAsync(
            write, System.Text.Json.JsonDocument.Parse("{}").RootElement, "scope", Caller, Ct);

        Assert.False(outcome.Granted);
    }

    [Fact]
    public async Task Law006_SilenceNeverHidesAFailure()
    {
        using var db = new SqliteTestDb();
        var engine = new SqliteDecisionEngine(db.Factory, new TestClock(Now));

        DecisionOption silent = new(
            DecisionMode.Silent, "say nothing", [],
            new OptionEvaluation(0.9, true, "LOW", 0, true, true), [], [], SilenceReason.NoiseLimit);

        Decision decision = await engine.EvaluateAsync(
            new DecisionThought("cycle-1", null, [silent], ["evidence/1"], 0.5, "LOW", ReportingFailure: true),
            new DecisionContext(true, [SilenceReason.NoiseLimit]), Ct);

        Assert.NotEqual(DecisionMode.Silent, decision.Mode);
    }

    // ================= LAW-007 — Event-mediated communication =================
    // Control: events carry identity, correlation, producer, date, classification and idempotency.

    [Fact]
    public async Task Law007_EveryEventCarriesTheRequiredFields()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(Now);
        var bus = new SqliteEventBus(db.Factory, new SqliteOutbox(new PermissiveEventCatalogue(), clock), clock);

        DomainEvent published = await bus.PublishAsync(
            new OutboxWrite("MemoryCreated", 1, "kernel", "corr-1", Sensitivity.Public,
                PayloadJson: """{"id":"m1"}""", IdempotencyKey: "k1"), Ct);

        Assert.False(string.IsNullOrWhiteSpace(published.EventId));
        Assert.Equal("corr-1", published.CorrelationId);
        Assert.Equal("kernel", published.Producer);
        Assert.False(string.IsNullOrWhiteSpace(published.OccurredAtUtc));
        Assert.Equal(Sensitivity.Public, published.SensitivityClass);
        Assert.Equal("k1", published.IdempotencyKey);
        Assert.False(string.IsNullOrWhiteSpace(published.IntegrityHash));
    }

    [Fact]
    public async Task Law007_AnEventWithoutACorrelationIdIsRefused()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(Now);
        var bus = new SqliteEventBus(db.Factory, new SqliteOutbox(new PermissiveEventCatalogue(), clock), clock);

        await Assert.ThrowsAsync<EventContractException>(() => bus.PublishAsync(
            new OutboxWrite("T", 1, "kernel", string.Empty, Sensitivity.Public, PayloadJson: "{}"), Ct));
    }

    [Fact]
    public async Task Law007_AConsumerDeclaresItsSchemaVersionAndPausesOnAnIncompatibleOne()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(Now);
        var bus = new SqliteEventBus(db.Factory, new SqliteOutbox(new PermissiveEventCatalogue(), clock), clock);

        Subscription subscription = await bus.SubscribeAsync(new Subscription(
            "sub-1", "indexer", ["MemoryCreated"], null, DeliveryMode.AtLeastOnce,
            0, SubscriptionStatus.Active, 3, MaxSchemaVersion: 1), Ct);

        await bus.PublishAsync(new OutboxWrite(
            "MemoryCreated", 2, "kernel", "corr-1", Sensitivity.Public, PayloadJson: "{}"), Ct);

        await bus.PumpAsync(new NullConsumer("indexer"), Ct);

        Subscription paused = await bus.SubscribeAsync(subscription, Ct);
        Assert.Equal(SubscriptionStatus.Paused, paused.Status);
        Assert.False(string.IsNullOrWhiteSpace(paused.Diagnosis));
    }

    // ================= LAW-008 — Identity integrity by Self Model =================
    // Control: the reasoning interface receives Identity and SelfModel from the Kernel and does not
    // reach persistence; a description is validated against a persisted identity before it is made;
    // the trace records SELF_MODEL(USED_FOR_SELF_DESCRIPTION) with the identity reference; and no
    // later argument, message or provider can replace the persisted name, purpose or Genome.

    [Fact]
    public void Law008_TheReasoningInterfaceCannotReachIdentityOrPersistence()
    {
        Type[] forbidden =
        [
            typeof(SelfModel), typeof(SafeSelfDescription), typeof(Genome), typeof(GenomeResolution),
            typeof(ISelfModel), typeof(IGenomeService), typeof(IGenomeSigner),
            typeof(IMemoryService), typeof(IMindStateService), typeof(IAuditStore),
        ];

        var violations = new List<string>();

        foreach (MethodInfo method in typeof(IReasoner).GetMethods())
        {
            IEnumerable<Type> touched = method.GetParameters()
                .Select(p => p.ParameterType)
                .Append(method.ReturnType)
                .SelectMany(Unwrap);

            foreach (Type type in touched.Where(t => forbidden.Contains(t)))
            {
                violations.Add($"IReasoner.{method.Name} touches {type.Name}");
            }
        }

        // A provider proposes an action from a catalogue. It is handed no identity, no self model
        // and no way to read one, so there is no path by which the language layer could become the
        // source of truth about who Aurora is.
        Assert.Empty(violations);

        // And what it may return has nowhere to put an identity claim either: an action id, an
        // input, a confidence and a provenance string. Adding a field here would be the way this
        // law is broken, so the shape itself is asserted.
        Assert.Equal(
            ["ActionId", "Input", "Confidence", "Via"],
            typeof(ReasonerProposal).GetProperties().Select(p => p.Name).Where(n => n != "EqualityContract"));

        static IEnumerable<Type> Unwrap(Type type) =>
            type.IsGenericType ? type.GetGenericArguments().Append(type) : [type];
    }

    [Fact]
    public async Task Law008_ASelfDescriptionRecordsWhichIdentityItWasDerivedFrom()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(Now);
        var audit = TestAudit(db, clock);
        ISelfModel self = SelfOver(db, clock, audit);

        SelfModel persisted = await self.RefreshAsync("local", Ct);
        SafeSelfDescription description = await self.DescribeAsync(Access, Ct);

        IReadOnlyList<AuditRecordView> records = await audit.QueryAsync(0, 50, Ct);

        AuditRecordView trace = Assert.Single(records, r => r.ActionId == "SELF_MODEL");

        Assert.Equal("USED_FOR_SELF_DESCRIPTION", trace.Outcome);

        // "with the identity reference": the record names the identity the description came from,
        // so a description read later can be tied back to the persisted model that produced it.
        Assert.Equal(persisted.IdentityRef, trace.Via);
        Assert.Contains(persisted.Version.ToString(CultureInfo.InvariantCulture), trace.Decision!);

        // "never with invented content or secrets": nothing the description itself said is copied
        // into the trace. Every field is a reference, a version or a fixed word.
        var written = string.Join("|", trace.Via, trace.Decision, trace.Outcome, trace.Reason);

        Assert.All(
            description.CanDo.Concat(description.CannotDo).Append(description.HealthSummary),
            said => Assert.DoesNotContain(said, written, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Law008_AModelThatNamesNoIdentityDescribesNothing()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(Now);
        ISelfModel self = SelfOver(db, clock, TestAudit(db, clock));

        await self.RefreshAsync("local", Ct);

        // Somebody with write access to the database clears the identity the model belongs to.
        // The row still looks like a self model, and every other field still reads correctly.
        await using (SqliteConnection connection = await db.Factory.OpenAsync(Ct))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE self_model SET identity_ref = '';";
            await command.ExecuteNonQueryAsync(Ct);
        }

        // Describing anyway would be Aurora asserting an identity it cannot derive from a
        // persisted one, which is the single thing this law forbids.
        await Assert.ThrowsAsync<SelfException>(() => self.DescribeAsync(Access, Ct));
    }

    [Fact]
    public async Task Law008_AnAlteredIdentityTemplateBirthsNoInstance()
    {
        using var db = new SqliteTestDb();
        using var signer = new EcdsaGenomeSigner(ECDsa.Create(ECCurve.NamedCurves.nistP256));
        var service = new SqliteGenomeService(
            db.Factory, signer, new StaticCapabilityRegistry([]), new TestClock(Now));

        Genome released = signer.Seal(new Genome(
            "genome-1", "Aurora Personal", "1.0.0", null, GenomeStatus.Released,
            "constitution-1", "laws-1", "identity/base", "personality/base", "development/base",
            MindSchemaVersion: 1, AllowedCapabilityIds: [], PolicyBundleRefs: ["policy/base"],
            DefaultLocales: ["pt-PT"], BootstrapConfigurationRef: "bootstrap/base",
            IntegrityHash: string.Empty, Signature: string.Empty));

        // The identity template is the genome's answer to who this instance is. Swapping it for
        // another one — the substitution this law exists to prevent — is refused on the way in.
        await Assert.ThrowsAsync<GenomeException>(() => service.RegisterAsync(
            released with { BaseIdentityTemplateRef = "identity/somebody-else" }, Ct));

        // And refused again on the way out, which is the half that matters: somebody who cannot go
        // through RegisterAsync can still write to the database, and a check that only ran at
        // registration would let that identity through at the moment an instance is created.
        await service.RegisterAsync(released, Ct);

        await using (SqliteConnection connection = await db.Factory.OpenAsync(Ct))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "UPDATE genome SET base_identity_template_ref = 'identity/somebody-else';";

            await command.ExecuteNonQueryAsync(Ct);
        }

        GenomeException refused = await Assert.ThrowsAsync<GenomeException>(
            () => service.ResolveAsync("genome-1", new InstallationContext("i1", [], [], []), Ct));

        Assert.Contains("signature", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NullConsumer(string name) : IEventConsumer
    {
        public string Name { get; } = name;

        public int Seen { get; private set; }

        public Task<ConsumeResult> ConsumeAsync(DomainEvent domainEvent, CancellationToken ct)
        {
            Seen++;
            return Task.FromResult(new ConsumeResult(ConsumeOutcome.Acked));
        }
    }
}
