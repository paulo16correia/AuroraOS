namespace Aurora.Core.Contracts;

/// <summary>Data classification carried by every event (RFC 03).</summary>
public static class Sensitivity
{
    public const string Public = "PUBLIC";
    public const string Private = "PRIVATE";
    public const string Confidential = "CONFIDENTIAL";
    public const string Secret = "SECRET";

    /// <summary>
    /// Classes that RFC 050 rule 3 forbids from travelling as an open payload; they must be
    /// carried by authoritative reference instead.
    /// </summary>
    public static bool RequiresReference(string sensitivity) =>
        sensitivity is Confidential or Secret;

    public static bool IsKnown(string sensitivity) =>
        sensitivity is Public or Private or Confidential or Secret;

    /// <summary>Ordering, so a caller's ceiling can be compared against a record's class.</summary>
    public static int Rank(string sensitivity) => sensitivity switch
    {
        Public => 0,
        Private => 1,
        Confidential => 2,
        Secret => 3,
        _ => int.MaxValue,
    };
}

/// <summary>
/// A domain fact or control signal on the Event Bus (RFC 050).
/// </summary>
/// <remarks>
/// Exactly one of <see cref="PayloadJson"/> or <see cref="PayloadRef"/> carries the body.
/// CONFIDENTIAL and SECRET events must use the reference: the bus carries facts, never the
/// sensitive content itself, and never replaces canonical storage.
/// </remarks>
public sealed record DomainEvent(
    string EventId,
    string Type,
    int SchemaVersion,
    string Producer,
    string OccurredAtUtc,
    string CorrelationId,
    string? CausationId,
    string? AggregateRef,
    string? PayloadJson,
    string? PayloadRef,
    string SensitivityClass,
    string? IdempotencyKey,
    string IntegrityHash);

public static class DeliveryMode
{
    /// <summary>Redelivered until acknowledged; the consumer must be idempotent (RFC 050 rule 2).</summary>
    public const string AtLeastOnce = "AT_LEAST_ONCE";
}

public static class SubscriptionStatus
{
    public const string Active = "ACTIVE";

    /// <summary>Held, with a diagnosis, rather than guessing at a schema it does not understand.</summary>
    public const string Paused = "PAUSED";

    public const string Failed = "FAILED";
}

/// <summary>A consumer's standing interest in a set of event types (RFC 050).</summary>
public sealed record Subscription(
    string Id,
    string Consumer,
    IReadOnlyList<string> EventTypes,
    string? FilterRef,
    string Mode,
    long Checkpoint,
    string Status,
    int MaxAttempts,
    int MaxSchemaVersion,
    string? Diagnosis = null);

public static class DeliveryStatus
{
    public const string Pending = "PENDING";
    public const string Acked = "ACKED";
    public const string Retrying = "RETRYING";
    public const string DeadLetter = "DEAD_LETTER";
    public const string Skipped = "SKIPPED";
}

/// <summary>One attempt to hand one event to one subscription (RFC 050).</summary>
public sealed record Delivery(
    string DeliveryId,
    string EventId,
    string SubscriptionId,
    int Attempt,
    string? DeliveredAtUtc,
    string Status,
    string? LastError = null);
