using Aurora.Adapters.Events;
using Aurora.Adapters.Incidents;
using Aurora.Adapters.Plugins;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// The two consumers Aurora runs in process, and what they do with an event (docs/adr/0063).
/// </summary>
/// <remarks>
/// Until the heartbeat existed nothing pumped the bus, so both of these were code with no
/// caller. What is asserted here is what happens once something delivers: a quarantine becomes an
/// incident, and a plugin is handed the events it subscribed to and no others.
/// </remarks>
public sealed class HeartbeatTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");

    private static DomainEvent Event(
        string type = EventCatalogue.PluginQuarantined,
        string? payload = """{"plugin_id":"acme/notes","reason":"SECRET_IN_OUTPUT: output resembled a credential"}""",
        string aggregate = "plugin/acme/notes",
        string sensitivity = Sensitivity.Private) => new(
        "event-1", type, 1, "kernel", "2026-01-01T00:00:00.0000000Z", "corr-1", null,
        aggregate, payload, null, sensitivity, "k1", "hash");

    [Fact]
    public async Task AQuarantineBecomesAnIncident()
    {
        var incidents = new RecordingIncidentService();

        ConsumeResult result = await new QuarantineIncidentConsumer(incidents)
            .ConsumeAsync(Event(), Ct);

        Assert.Equal(ConsumeOutcome.Acked, result.Outcome);

        SecurityEvent raised = Assert.Single(incidents.Opened);

        // A secret in a plugin's output is worth revoking the owner's standing consent over.
        Assert.Equal(SecuritySeverity.High, raised.Severity);
        Assert.Equal(SecurityEventType.SecretExposed, raised.Type);

        // Named, so containment disables this plugin and not every plugin.
        Assert.Equal("plugin/acme/notes", raised.ResourceRef);
        Assert.Equal("event-1", raised.EvidenceRef);
    }

    [Fact]
    public async Task AnOrdinaryQuarantineIsNotTreatedAsASecretLeak()
    {
        var incidents = new RecordingIncidentService();

        await new QuarantineIncidentConsumer(incidents).ConsumeAsync(
            Event(payload: """{"plugin_id":"acme/notes","reason":"3 consecutive failures"}"""), Ct);

        // A plugin that failed three times is a different kind of event from one that returned a
        // credential, and only one of them is worth waking somebody for.
        SecurityEvent raised = Assert.Single(incidents.Opened);
        Assert.Equal(SecuritySeverity.Medium, raised.Severity);
        Assert.Equal(SecurityEventType.UndeclaredBehaviour, raised.Type);
    }

    [Fact]
    public async Task AnEventWithAnUnreadablePayloadStillOpensAnIncident()
    {
        var incidents = new RecordingIncidentService();

        await new QuarantineIncidentConsumer(incidents).ConsumeAsync(Event(payload: "not json"), Ct);

        // Losing an incident because a field was renamed would be the worst possible trade.
        Assert.Single(incidents.Opened);
    }

    [Fact]
    public async Task EverythingElseIsSkippedRatherThanGuessedAt()
    {
        var incidents = new RecordingIncidentService();

        ConsumeResult result = await new QuarantineIncidentConsumer(incidents)
            .ConsumeAsync(Event(type: EventCatalogue.MemoryRevised), Ct);

        Assert.Equal(ConsumeOutcome.Skipped, result.Outcome);
        Assert.Empty(incidents.Opened);
    }

    [Fact]
    public void TheConsumerSubscribesToTheOneTypeItHandles()
    {
        // Declared on the consumer rather than at registration, so the two cannot drift: one that
        // started handling a new type and forgot to widen a subscription written elsewhere would
        // silently stop being given the thing it was changed to handle.
        Assert.Equal(
            [EventCatalogue.PluginQuarantined],
            new QuarantineIncidentConsumer(new RecordingIncidentService()).EventTypes);
    }

    [Fact]
    public async Task ThePumpDeliversWhatWasPublishedAfterTheCheckpoint()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(Now);
        var bus = new SqliteEventBus(
            db.Factory, new SqliteOutbox(new PermissiveEventCatalogue(), clock), clock);

        var incidents = new RecordingIncidentService();
        var consumer = new QuarantineIncidentConsumer(incidents);

        await bus.SubscribeAsync(new Subscription(
            "heartbeat:incidents", consumer.Name, consumer.EventTypes, null,
            DeliveryMode.AtLeastOnce, 0, SubscriptionStatus.Active, 3, 1), Ct);

        await bus.PublishAsync(new OutboxWrite(
            EventCatalogue.PluginQuarantined, 1, EventCatalogue.Producers.Kernel, "corr-1",
            Sensitivity.Private, AggregateRef: "plugin/acme/notes",
            PayloadJson: """{"reason":"3 consecutive failures"}""", IdempotencyKey: "k1"), Ct);

        await bus.PumpAsync(consumer, Ct);
        Assert.Single(incidents.Opened);

        // And a second pump with nothing new delivers nothing: the checkpoint moved, so an
        // instance that beats every five minutes does not re-open the same incident every time.
        await bus.PumpAsync(consumer, Ct);
        Assert.Single(incidents.Opened);
    }

    // ---- a plugin is handed the events it subscribed to, and no others ----

    /// <summary>A registry that answers from what the test set up, and remembers the calls.</summary>
    private sealed class SubscribedRegistry(
        IReadOnlyList<PluginInstallation> installed,
        IReadOnlyDictionary<string, string[]> subscriptions) : IPluginRegistry
    {
        public List<PluginInvocation> Calls { get; } = [];

        public Task<PluginResult> InvokeAsync(PluginInvocation invocation, CancellationToken ct)
        {
            Calls.Add(invocation);
            return Task.FromResult(new PluginResult(true, "{}", null, "completed", 1));
        }

        public Task<IReadOnlyList<string>> PermittedSubscriptionsAsync(string pluginId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(
                subscriptions.TryGetValue(pluginId, out var types) ? types : []);

        public Task<IReadOnlyList<PluginInstallation>> ListAsync(CancellationToken ct) =>
            Task.FromResult(installed);

        public Task<PluginVerification> VerifyAsync(PluginManifest manifest, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PluginInstallation> InstallAsync(
            PluginManifest manifest, IReadOnlyList<string> granted, string approvalRef, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PluginInstallation> UpdateAsync(PluginManifest manifest, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PluginInstallation> DisableAsync(string installationId, string actor, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PluginInstallation> ReleaseAsync(
            string installationId, string actor, string reason, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PluginInstallation?> GetAsync(string pluginId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PluginInstallation> RemoveAsync(string installationId, string actor, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PluginInstallation> InstallAsync(
            PluginManifest manifest, IReadOnlyList<string> grantedPermissions,
            IReadOnlyList<string> grantedEndpoints, bool grantGpu, string approvalRef,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static PluginInstallation Installed(string id, string status = InstallationStatus.Installed) =>
        new($"install-{id}", id, "1.0.0", "acme", status, [], "{}", "then", "then", 0);

    [Fact]
    public async Task OnlyAPluginThatSubscribedIsHandedTheEvent()
    {
        var registry = new SubscribedRegistry(
            [Installed("acme/listens"), Installed("acme/ignores")],
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["acme/listens"] = [EventCatalogue.MemoryRevised],
                ["acme/ignores"] = [EventCatalogue.GoalDrafted],
            });

        await new PluginEventConsumer(registry).ConsumeAsync(
            Event(type: EventCatalogue.MemoryRevised, aggregate: "memory/1"), Ct);

        PluginInvocation call = Assert.Single(registry.Calls);
        Assert.Equal("acme/listens", call.PluginId);

        // One key rather than one per event type, so the routing a plugin would otherwise
        // duplicate stays in one place. The type is in the payload.
        Assert.Equal(PluginEventConsumer.OnEvent, call.CapabilityKey);
        Assert.Contains(EventCatalogue.MemoryRevised, call.InputJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AQuarantinedPluginIsNotHandedAnything()
    {
        var registry = new SubscribedRegistry(
            [Installed("acme/held", InstallationStatus.Quarantined)],
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["acme/held"] = [EventCatalogue.MemoryRevised],
            });

        await new PluginEventConsumer(registry).ConsumeAsync(
            Event(type: EventCatalogue.MemoryRevised), Ct);

        // Being held means held. Delivery is not a smaller thing than a call: the plugin runs
        // either way.
        Assert.Empty(registry.Calls);
    }

    [Fact]
    public async Task AClassifiedEventTravelsWithoutItsPayload()
    {
        var registry = new SubscribedRegistry(
            [Installed("acme/listens")],
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["acme/listens"] = [EventCatalogue.MemoryRevised],
            });

        await new PluginEventConsumer(registry).ConsumeAsync(
            Event(
                type: EventCatalogue.MemoryRevised,
                payload: """{"secret":"the thing itself"}""",
                sensitivity: Sensitivity.Confidential),
            Ct);

        PluginInvocation call = Assert.Single(registry.Calls);

        // RFC 050 rule 3: a classified payload travels by reference, and a plugin is the last
        // thing that should be handed one directly. It still learns that something happened.
        Assert.DoesNotContain("the thing itself", call.InputJson, StringComparison.Ordinal);
        Assert.Contains(EventCatalogue.MemoryRevised, call.InputJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnePluginFailingDoesNotUndeliverTheEventForTheOthers()
    {
        var registry = new FailingRegistry();

        ConsumeResult result = await new PluginEventConsumer(registry).ConsumeAsync(
            Event(type: EventCatalogue.MemoryRevised), Ct);

        // Retrying the whole fan-out would punish the plugins that handled it correctly by
        // handing them the same event again. The registry has already counted the failure against
        // the one that failed, and three of them open its circuit.
        Assert.Equal(ConsumeOutcome.Acked, result.Outcome);
        Assert.Contains("acme/broken", result.Error!, StringComparison.Ordinal);
    }

    private sealed class FailingRegistry : IPluginRegistry
    {
        public Task<PluginResult> InvokeAsync(PluginInvocation invocation, CancellationToken ct) =>
            Task.FromResult(new PluginResult(false, null, "nonzero_exit", "exited 1", 1));

        public Task<IReadOnlyList<string>> PermittedSubscriptionsAsync(string pluginId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([EventCatalogue.MemoryRevised]);

        public Task<IReadOnlyList<PluginInstallation>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PluginInstallation>>([Installed("acme/broken")]);

        public Task<PluginVerification> VerifyAsync(PluginManifest manifest, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PluginInstallation> InstallAsync(
            PluginManifest manifest, IReadOnlyList<string> granted, string approvalRef, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PluginInstallation> UpdateAsync(PluginManifest manifest, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PluginInstallation> DisableAsync(string installationId, string actor, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PluginInstallation> ReleaseAsync(
            string installationId, string actor, string reason, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PluginInstallation?> GetAsync(string pluginId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PluginInstallation> RemoveAsync(string installationId, string actor, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PluginInstallation> InstallAsync(
            PluginManifest manifest, IReadOnlyList<string> grantedPermissions,
            IReadOnlyList<string> grantedEndpoints, bool grantGpu, string approvalRef,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
