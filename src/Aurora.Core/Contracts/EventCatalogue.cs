namespace Aurora.Core.Contracts;

/// <summary>
/// A published contract for one event type at one schema version (LAW-007).
/// </summary>
/// <remarks>
/// LAW-007's verifiable control is that <i>each producer declares events</i> and each consumer
/// declares its subscriptions and schema version. A declaration that lives only in documentation
/// is a hope; this one is checked by the outbox, so an undeclared event cannot be published at all.
/// </remarks>
public sealed record EventContract(
    string Type,
    int SchemaVersion,
    /// <summary>The component allowed to emit this. Another producer using the type is refused.</summary>
    string Producer,
    string SensitivityClass,
    /// <summary>What the payload carries, in words, so a consumer knows what it is subscribing to.</summary>
    string Payload,
    IReadOnlyList<string> Consumers);

/// <summary>
/// Every event Aurora may publish (LAW-007, architecture review condition 5).
/// </summary>
/// <remarks>
/// Deliberately a closed, compile-time list. An event type that is not here cannot be published,
/// which is what stops the bus quietly becoming a place where anything can be asserted about
/// anything — including by a caller reaching the ingress endpoint from outside.
/// </remarks>
public static class EventCatalogue
{
    public const string ConversationTurnReceived = "ConversationTurnReceived";
    public const string KernelCommandAccepted = "KernelCommandAccepted";
    public const string ApprovalDecided = "ApprovalDecided";
    public const string MemoryRevised = "MemoryRevised";
    public const string MemoryForgotten = "MemoryForgotten";
    public const string JobDue = "JobDue";
    public const string ScheduleRunsMissed = "ScheduleRunsMissed";
    public const string ScheduleDisabled = "ScheduleDisabled";
    public const string MaintenancePassCompleted = "MaintenancePassCompleted";
    public const string ReviewRequested = "ReviewRequested";
    public const string ExternalObservationReported = "ExternalObservationReported";

    /// <summary>Producers, named once so a typo cannot invent one.</summary>
    public static class Producers
    {
        public const string Kernel = "kernel";
        public const string Pilot = "pilot";
        public const string Scheduler = "scheduler";
        public const string Maintenance = "maintenance";
        public const string Review = "review";
        public const string Memory = "memory";

        /// <summary>The ingress endpoint. The only producer reachable from outside Aurora.</summary>
        public const string Api = "api";
    }

    public static IReadOnlyList<EventContract> Declared { get; } =
    [
        new(ConversationTurnReceived, 1, Producers.Pilot, Sensitivity.Private,
            "length of the turn; the words themselves stay in the conversation record",
            ["attention", "audit"]),

        new(KernelCommandAccepted, 1, Producers.Kernel, Sensitivity.Private,
            "action_id and how it was resolved",
            ["audit", "review"]),

        new(ApprovalDecided, 1, Producers.Kernel, Sensitivity.Private,
            "approval_id, the decision, and the action it was for",
            ["ui", "audit", "review"]),

        new(MemoryRevised, 1, Producers.Memory, Sensitivity.Private,
            "memory_id, the operation and who asked for it; never the content",
            ["ui", "audit", "reflection"]),

        new(MemoryForgotten, 1, Producers.Memory, Sensitivity.Private,
            "memory_id and what the retraction actually removed; never the content",
            ["ui", "audit", "reflection"]),

        new(JobDue, 1, Producers.Scheduler, Sensitivity.Private,
            "run_id and the schedule's target; the run itself is not started by this",
            ["cycle", "review"]),

        new(ScheduleRunsMissed, 1, Producers.Scheduler, Sensitivity.Private,
            "how many occurrences were missed and under which policy",
            ["ui", "needs", "review"]),

        new(ScheduleDisabled, 1, Producers.Scheduler, Sensitivity.Private,
            "the status it moved to and why it stopped firing",
            ["ui", "needs", "review"]),

        new(MaintenancePassCompleted, 1, Producers.Maintenance, Sensitivity.Private,
            "counts of what one upkeep pass expired, noticed and reconciled",
            ["review", "metrics"]),

        new(ReviewRequested, 1, Producers.Review, Sensitivity.Private,
            "the audit cursor the review started from",
            ["audit"]),

        // The one ingress type. A UI or channel normalises something it saw and reports it; it is
        // an observation, not a fact Aurora has accepted, and nothing subscribes to it as truth.
        new(ExternalObservationReported, 1, Producers.Api, Sensitivity.Private,
            "what an outside surface observed, as reported; unverified by construction",
            ["perception"]),
    ];

    public static bool TryGet(string type, int schemaVersion, out EventContract? contract)
    {
        contract = Declared.FirstOrDefault(
            c => string.Equals(c.Type, type, StringComparison.Ordinal) && c.SchemaVersion == schemaVersion);

        return contract is not null;
    }

    /// <summary>The types a given producer is allowed to emit.</summary>
    public static IReadOnlyList<EventContract> For(string producer) =>
        Declared.Where(c => string.Equals(c.Producer, producer, StringComparison.Ordinal)).ToList();
}
