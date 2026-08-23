using Aurora.Adapters.Events;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>Conformance tests for RFC 050, one group per mandatory rule and error case.</summary>
public sealed class EventBusTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed class RecordingConsumer : IEventConsumer
    {
        private readonly Func<DomainEvent, ConsumeResult> _behaviour;

        public RecordingConsumer(string name, Func<DomainEvent, ConsumeResult>? behaviour = null)
        {
            Name = name;
            _behaviour = behaviour ?? (_ => new ConsumeResult(ConsumeOutcome.Acked));
        }

        public string Name { get; }

        public List<string> Seen { get; } = [];

        public Task<ConsumeResult> ConsumeAsync(DomainEvent domainEvent, CancellationToken ct)
        {
            Seen.Add(domainEvent.EventId);
            return Task.FromResult(_behaviour(domainEvent));
        }
    }

    private static SqliteEventBus Bus(SqliteTestDb db)
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        return new SqliteEventBus(db.Factory, new SqliteOutbox(clock), clock);
    }

    private static OutboxWrite Write(string type = "MemoryCreated", int schemaVersion = 1) =>
        new(type, schemaVersion, "kernel", "corr-1", Sensitivity.Public, PayloadJson: """{"a":1}""");

    private static Subscription Sub(
        string consumer = "indexer", int maxAttempts = 3, int maxSchemaVersion = 1, params string[] types) =>
        new("sub-1", consumer, types.Length == 0 ? ["MemoryCreated"] : types, null,
            DeliveryMode.AtLeastOnce, 0, SubscriptionStatus.Active, maxAttempts, maxSchemaVersion);

    // ---- Rule 1: producers write state and outbox in the same transaction ----

    [Fact]
    public async Task CommittedScope_MakesTheEventVisible()
    {
        using var db = new SqliteTestDb();
        var bus = Bus(db);
        await bus.SubscribeAsync(Sub(), Ct);

        await using (IDbTransactionScope scope = await bus.BeginAsync(Ct))
        {
            await new SqliteOutbox(new TestClock(DateTimeOffset.UnixEpoch)).EnqueueAsync(Write(), scope, Ct);
            await scope.CommitAsync(Ct);
        }

        var consumer = new RecordingConsumer("indexer");
        Assert.Equal(1, await bus.PumpAsync(consumer, Ct));
        Assert.Single(consumer.Seen);
    }

    [Fact]
    public async Task RolledBackScope_LosesTheEventWithTheStateChange()
    {
        using var db = new SqliteTestDb();
        var bus = Bus(db);
        await bus.SubscribeAsync(Sub(), Ct);

        // Disposed without committing: an event must never describe a change that did not happen.
        await using (IDbTransactionScope scope = await bus.BeginAsync(Ct))
        {
            await new SqliteOutbox(new TestClock(DateTimeOffset.UnixEpoch)).EnqueueAsync(Write(), scope, Ct);
        }

        var consumer = new RecordingConsumer("indexer");
        Assert.Equal(0, await bus.PumpAsync(consumer, Ct));
        Assert.Empty(consumer.Seen);
    }

    [Fact]
    public async Task FanOut_IsReExecutable()
    {
        using var db = new SqliteTestDb();
        var bus = Bus(db);
        await bus.SubscribeAsync(Sub(), Ct);
        await bus.PublishAsync(Write(), Ct);

        var consumer = new RecordingConsumer("indexer");
        await bus.PumpAsync(consumer, Ct);
        await bus.PumpAsync(consumer, Ct);

        // Repeating publication after a crash must not produce a second delivery.
        Assert.Single(consumer.Seen);
        Assert.Single(await bus.ReplayAsync("sub-1", 0, Ct));
    }

    // ---- Rule 2: idempotent consumers, declared types, no assumed global order ----

    [Fact]
    public async Task AckedEvent_IsNotRedelivered()
    {
        using var db = new SqliteTestDb();
        var bus = Bus(db);
        await bus.SubscribeAsync(Sub(), Ct);
        await bus.PublishAsync(Write(), Ct);

        var consumer = new RecordingConsumer("indexer");
        await bus.PumpAsync(consumer, Ct);
        await bus.PumpAsync(consumer, Ct);

        Assert.Single(consumer.Seen);
    }

    [Fact]
    public async Task ASubscriptionOnlyReceivesItsDeclaredTypes()
    {
        using var db = new SqliteTestDb();
        var bus = Bus(db);
        await bus.SubscribeAsync(Sub(types: "MemoryCreated"), Ct);
        await bus.PublishAsync(Write("MemoryCreated"), Ct);
        await bus.PublishAsync(Write("PlanRevised"), Ct);

        var consumer = new RecordingConsumer("indexer");
        await bus.PumpAsync(consumer, Ct);

        Assert.Single(consumer.Seen);
    }

    // ---- Rule 3: big or sensitive data travels by reference ----

    [Theory]
    [InlineData(Sensitivity.Confidential)]
    [InlineData(Sensitivity.Secret)]
    public async Task SensitiveEvent_MayNotCarryAnOpenPayload(string sensitivity)
    {
        using var db = new SqliteTestDb();
        var bus = Bus(db);

        await Assert.ThrowsAsync<EventContractException>(() => bus.PublishAsync(
            new OutboxWrite("MemoryCreated", 1, "kernel", "corr-1", sensitivity, PayloadJson: """{"a":1}"""), Ct));
    }

    [Theory]
    [InlineData(Sensitivity.Confidential)]
    [InlineData(Sensitivity.Secret)]
    public async Task SensitiveEvent_TravelsByReference(string sensitivity)
    {
        using var db = new SqliteTestDb();
        var bus = Bus(db);

        DomainEvent published = await bus.PublishAsync(
            new OutboxWrite("MemoryCreated", 1, "kernel", "corr-1", sensitivity, PayloadRef: "memory://42"), Ct);

        Assert.Equal("memory://42", published.PayloadRef);
        Assert.Null(published.PayloadJson);
    }

    [Fact]
    public async Task AnEventCarriesExactlyOnePayloadForm()
    {
        using var db = new SqliteTestDb();
        var bus = Bus(db);

        await Assert.ThrowsAsync<EventContractException>(() => bus.PublishAsync(
            new OutboxWrite("T", 1, "kernel", "c", Sensitivity.Public), Ct));

        await Assert.ThrowsAsync<EventContractException>(() => bus.PublishAsync(
            new OutboxWrite("T", 1, "kernel", "c", Sensitivity.Public, PayloadJson: "{}", PayloadRef: "r"), Ct));
    }

    // ---- LAW-007 verifiable controls ----

    [Fact]
    public async Task EventCarriesIdentityCorrelationProducerDateAndClassification()
    {
        using var db = new SqliteTestDb();
        DomainEvent published = await Bus(db).PublishAsync(Write(), Ct);

        Assert.False(string.IsNullOrWhiteSpace(published.EventId));
        Assert.Equal("corr-1", published.CorrelationId);
        Assert.Equal("kernel", published.Producer);
        Assert.False(string.IsNullOrWhiteSpace(published.OccurredAtUtc));
        Assert.Equal(Sensitivity.Public, published.SensitivityClass);
        Assert.False(string.IsNullOrWhiteSpace(published.IntegrityHash));
    }

    [Fact]
    public async Task AnEventWithoutACorrelationId_IsRefused()
    {
        using var db = new SqliteTestDb();

        await Assert.ThrowsAsync<EventContractException>(() => Bus(db).PublishAsync(
            new OutboxWrite("T", 1, "kernel", string.Empty, Sensitivity.Public, PayloadJson: "{}"), Ct));
    }

    // ---- Rule 4: repeated failures reach an auditable dead-letter queue ----

    [Fact]
    public async Task RepeatedFailures_EndInTheDeadLetterQueue()
    {
        using var db = new SqliteTestDb();
        var bus = Bus(db);
        await bus.SubscribeAsync(Sub(maxAttempts: 3), Ct);
        await bus.PublishAsync(Write(), Ct);

        var consumer = new RecordingConsumer("indexer", _ => new ConsumeResult(ConsumeOutcome.Retry, "boom"));
        for (var i = 0; i < 3; i++)
        {
            await bus.PumpAsync(consumer, Ct);
        }

        IReadOnlyList<Delivery> dead = await bus.DeadLettersAsync(Ct);
        Assert.Single(dead);
        Assert.Equal("boom", dead[0].LastError);
    }

    [Fact]
    public async Task ThrowingConsumer_IsRetriedRatherThanLosingTheEvent()
    {
        using var db = new SqliteTestDb();
        var bus = Bus(db);
        await bus.SubscribeAsync(Sub(maxAttempts: 2), Ct);
        await bus.PublishAsync(Write(), Ct);

        var consumer = new RecordingConsumer("indexer", _ => throw new InvalidOperationException("kaboom"));
        await bus.PumpAsync(consumer, Ct);
        await bus.PumpAsync(consumer, Ct);

        Assert.Single(await bus.DeadLettersAsync(Ct));
    }

    [Fact]
    public async Task AfterDeadLettering_TheSubscriptionMovesOn()
    {
        using var db = new SqliteTestDb();
        var bus = Bus(db);
        await bus.SubscribeAsync(Sub(maxAttempts: 1), Ct);
        DomainEvent poison = await bus.PublishAsync(Write(), Ct);
        await bus.PublishAsync(Write(), Ct);

        // One poisonous event must not wedge the stream behind it forever.
        var consumer = new RecordingConsumer(
            "indexer",
            e => e.EventId == poison.EventId
                ? new ConsumeResult(ConsumeOutcome.Retry, "poison")
                : new ConsumeResult(ConsumeOutcome.Acked));

        await bus.PumpAsync(consumer, Ct);
        await bus.PumpAsync(consumer, Ct);

        Assert.Single(await bus.DeadLettersAsync(Ct));
        Assert.Contains(consumer.Seen, id => id != poison.EventId);
    }

    // ---- Error case: a schema the consumer does not understand ----

    [Fact]
    public async Task AnUnknownSchemaVersion_PausesTheSubscriptionWithADiagnosis()
    {
        using var db = new SqliteTestDb();
        var bus = Bus(db);
        Subscription subscription = await bus.SubscribeAsync(Sub(maxSchemaVersion: 1), Ct);
        await bus.PublishAsync(Write(schemaVersion: 2), Ct);

        var consumer = new RecordingConsumer("indexer");
        await bus.PumpAsync(consumer, Ct);

        // It must not guess at fields it does not know.
        Assert.Empty(consumer.Seen);

        Subscription paused = await bus.SubscribeAsync(subscription, Ct);
        Assert.Equal(SubscriptionStatus.Paused, paused.Status);
        Assert.Contains("v2", paused.Diagnosis!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APausedSubscriptionStopsReceiving_AndKeepsTheEventWaiting()
    {
        using var db = new SqliteTestDb();
        var bus = Bus(db);
        await bus.SubscribeAsync(Sub(maxSchemaVersion: 1), Ct);
        await bus.PublishAsync(Write(schemaVersion: 2), Ct);

        var consumer = new RecordingConsumer("indexer");
        await bus.PumpAsync(consumer, Ct);
        await bus.PumpAsync(consumer, Ct);
        Assert.Empty(consumer.Seen);

        // Upgraded consumer: re-subscribing reactivates it and the event is still there.
        await bus.SubscribeAsync(
            new Subscription("sub-1", "indexer", ["MemoryCreated"], null, DeliveryMode.AtLeastOnce,
                0, SubscriptionStatus.Active, 3, MaxSchemaVersion: 2), Ct);
        await bus.PumpAsync(consumer, Ct);

        Assert.Single(consumer.Seen);
    }

    // ---- Replay and ack ----

    [Fact]
    public async Task Replay_ReturnsDeliveriesFromACursor()
    {
        using var db = new SqliteTestDb();
        var bus = Bus(db);
        await bus.SubscribeAsync(Sub(), Ct);
        await bus.PublishAsync(Write(), Ct);
        await bus.PublishAsync(Write(), Ct);
        await bus.PumpAsync(new RecordingConsumer("indexer"), Ct);

        Assert.Equal(2, (await bus.ReplayAsync("sub-1", 0, Ct)).Count);
        Assert.Single(await bus.ReplayAsync("sub-1", 1, Ct));
    }

    [Fact]
    public async Task Ack_MarksTheDeliveryAcknowledged()
    {
        using var db = new SqliteTestDb();
        var bus = Bus(db);
        await bus.SubscribeAsync(Sub(maxAttempts: 5), Ct);
        await bus.PublishAsync(Write(), Ct);
        await bus.PumpAsync(new RecordingConsumer("indexer", _ => new ConsumeResult(ConsumeOutcome.Retry)), Ct);

        Delivery pending = (await bus.ReplayAsync("sub-1", 0, Ct))[0];
        Delivery? acked = await bus.AckAsync(pending.DeliveryId, Ct);

        Assert.Equal(DeliveryStatus.Acked, acked!.Status);
    }
}
