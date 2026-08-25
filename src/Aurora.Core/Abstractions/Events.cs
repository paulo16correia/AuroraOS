using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>Raised when a publication would break one of the RFC 050 mandatory rules.</summary>
public sealed class EventContractException : Exception
{
    public EventContractException(string message) : base(message)
    {
    }
}

/// <summary>What a consumer did with an event.</summary>
public enum ConsumeOutcome
{
    /// <summary>Processed, or recognised as already processed. The delivery is acknowledged.</summary>
    Acked,

    /// <summary>Not applicable to this consumer; acknowledged without work.</summary>
    Skipped,

    /// <summary>Transient failure. The delivery is retried until the subscription's ceiling.</summary>
    Retry,
}

public sealed record ConsumeResult(ConsumeOutcome Outcome, string? Error = null);

/// <summary>
/// A consumer of domain events. Implementations must be idempotent: at-least-once delivery means
/// the same event can arrive more than once, and no global order is guaranteed between aggregates
/// (RFC 050 rule 2).
/// </summary>
public interface IEventConsumer
{
    string Name { get; }

    Task<ConsumeResult> ConsumeAsync(DomainEvent domainEvent, CancellationToken ct);
}

/// <summary>
/// The transactional outbox (RFC 050 rule 1). A producer writes its state change and the event in
/// the <b>same</b> transaction, so an event can never describe a state change that did not commit,
/// and a committed change can never lose its event.
/// </summary>
public interface IOutbox
{
    /// <summary>
    /// Enqueues an event inside a transaction the caller owns and will commit. The returned event
    /// carries its generated identity and integrity hash.
    /// </summary>
    Task<DomainEvent> EnqueueAsync(OutboxWrite write, IDbTransactionScope scope, CancellationToken ct);
}

/// <summary>The fields a producer supplies; identity, timestamp and hash are assigned by the outbox.</summary>
public sealed record OutboxWrite(
    string Type,
    int SchemaVersion,
    string Producer,
    string CorrelationId,
    string SensitivityClass,
    string? CausationId = null,
    string? AggregateRef = null,
    string? PayloadJson = null,
    string? PayloadRef = null,
    string? IdempotencyKey = null);

/// <summary>
/// A transaction the producer owns, so state and outbox commit together. Kept abstract in Core so
/// the domain never names a database type.
/// </summary>
public interface IDbTransactionScope : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct);
}

/// <summary>
/// The event types this deployment is allowed to publish (LAW-007).
/// </summary>
/// <remarks>
/// A dependency rather than a static lookup so the outbox can be tested on its own mechanics, and
/// so a deployment could in principle declare more. The production wiring is
/// <c>DeclaredEventCatalogue</c>, which reads the compile-time list and nothing else; an
/// architecture test asserts that is what the server registers.
/// </remarks>
public interface IEventCatalogue
{
    /// <summary>
    /// Whether this write is one the deployment declared, and if not, what is wrong with it.
    /// </summary>
    /// <remarks>
    /// The catalogue answers about the whole write rather than about the type alone, because the
    /// declaration covers who may emit it and at what classification — and a mismatch on either is
    /// the interesting failure, not a missing name.
    /// </remarks>
    bool TryValidate(OutboxWrite write, out string? violation);

    IReadOnlyList<EventContract> Declared { get; }
}

/// <summary>The Event Bus surface defined by RFC 050.</summary>
public interface IEventBus
{
    /// <summary>Opens a scope a producer can write both its state and its events into.</summary>
    Task<IDbTransactionScope> BeginAsync(CancellationToken ct);

    /// <summary>
    /// Publishes a single event with no other state to commit alongside it. A convenience over
    /// <see cref="BeginAsync"/>; it still goes through the outbox, so rule 1 holds either way.
    /// </summary>
    Task<DomainEvent> PublishAsync(OutboxWrite write, CancellationToken ct);

    /// <summary>Registers, or re-registers, a consumer's interest. Idempotent by subscription id.</summary>
    Task<Subscription> SubscribeAsync(Subscription subscription, CancellationToken ct);

    /// <summary>
    /// Fans committed events out to matching subscriptions and hands them to the consumer,
    /// acknowledging, retrying or dead-lettering each. Re-executable: RFC 050 rule 1 requires that
    /// publication can be repeated after a crash without duplicating deliveries.
    /// </summary>
    Task<int> PumpAsync(IEventConsumer consumer, CancellationToken ct);

    /// <summary>Marks a delivery acknowledged.</summary>
    Task<Delivery?> AckAsync(string deliveryId, CancellationToken ct);

    /// <summary>Re-reads a subscription's deliveries from a cursor, for recovery and debugging.</summary>
    Task<IReadOnlyList<Delivery>> ReplayAsync(string subscriptionId, long cursor, CancellationToken ct);

    /// <summary>The auditable dead-letter queue; nothing is ever silently discarded (rule 4).</summary>
    Task<IReadOnlyList<Delivery>> DeadLettersAsync(CancellationToken ct);

    /// <summary>
    /// Reads committed events after a cursor, for the authorized stream of RFC 10.
    /// </summary>
    /// <remarks>
    /// Filtering happens here rather than in the caller (RFC 10 rule 3), and an event above the
    /// caller's ceiling is omitted entirely rather than returned redacted — a redacted entry still
    /// discloses that something classified happened, which rule 4 does not permit.
    /// </remarks>
    Task<IReadOnlyList<SequencedEvent>> ReadAsync(
        long afterSequence, int limit, string maxSensitivity, CancellationToken ct);
}
