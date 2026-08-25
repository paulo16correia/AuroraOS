namespace Aurora.Core.Contracts;

public static class SignalKind
{
    public const string Message = "MESSAGE";
    public const string Alert = "ALERT";
    public const string Change = "CHANGE";
    public const string Schedule = "SCHEDULE";
    public const string Health = "HEALTH";
    public const string Opportunity = "OPPORTUNITY";

    public static bool IsKnown(string kind) =>
        kind is Message or Alert or Change or Schedule or Health or Opportunity;
}

public static class SignalSeverity
{
    public const string Info = "INFO";
    public const string Low = "LOW";
    public const string Medium = "MEDIUM";
    public const string High = "HIGH";
    public const string Critical = "CRITICAL";

    public static int Rank(string severity) => severity switch
    {
        Info => 0,
        Low => 1,
        Medium => 2,
        High => 3,
        Critical => 4,
        _ => -1,
    };

    public static bool IsKnown(string severity) => Rank(severity) >= 0;
}

/// <summary>How much of Aurora's attention a signal is allowed to take (RFC 030).</summary>
public static class Interruptibility
{
    /// <summary>Wait its turn.</summary>
    public const string Queue = "QUEUE";

    /// <summary>Wait, but jump in as soon as nothing else is being worked on.</summary>
    public const string FocusWhenIdle = "FOCUS_WHEN_IDLE";

    /// <summary>Stop what is happening. Requires a policy threshold.</summary>
    public const string Interrupt = "INTERRUPT";

    /// <summary>Stop everything. Requires a policy threshold.</summary>
    public const string Emergency = "EMERGENCY";

    /// <summary>Whether this level takes attention away from work already in progress.</summary>
    public static bool Interrupts(string level) => level is Interrupt or Emergency;

    public static bool IsKnown(string level) =>
        level is Queue or FocusWhenIdle or Interrupt or Emergency;
}

public static class SignalStatus
{
    public const string New = "NEW";
    public const string Queued = "QUEUED";
    public const string Focused = "FOCUSED";

    /// <summary>Held back — a duplicate, or over the rate limit. Recorded, never discarded.</summary>
    public const string Suppressed = "SUPPRESSED";

    public const string Expired = "EXPIRED";
    public const string Resolved = "RESOLVED";
}

/// <summary>Why a signal was rated or routed as it was. Closed set, so a reason is checkable.</summary>
public static class SignalReason
{
    public const string Duplicate = "DUPLICATE_WITHIN_WINDOW";
    public const string RateLimited = "RATE_LIMITED";
    public const string BelowInterruptThreshold = "BELOW_INTERRUPT_THRESHOLD";
    public const string ThresholdMet = "INTERRUPT_THRESHOLD_MET";
    public const string NothingInProgress = "NOTHING_IN_PROGRESS";
    public const string Expired = "EXPIRED_BEFORE_ROUTING";
}

/// <summary>
/// A temporary assessment of relevance and urgency derived from a fact (RFC 030).
/// </summary>
/// <remarks>
/// Not an event. An event is a standardised, immutable fact; a signal is an opinion about how much
/// that fact matters right now, and it expires. Nothing here names a capability or a permission,
/// because urgency changes attention and order of evaluation and nothing else (rule 2).
/// </remarks>
public sealed record Signal(
    string Id,
    string SourceEventRef,
    string Kind,
    string Severity,
    double Urgency,
    double Relevance,
    double Confidence,
    IReadOnlyList<string> TargetRefs,
    string CreatedAtUtc,
    string ExpiresAtUtc,
    string Interruptibility,
    string Status,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<string> PolicyRefs,
    string? ResolutionRef = null);

/// <summary>What a classifier proposes about a fact, before it becomes a signal.</summary>
public sealed record SignalClassification(
    string Kind,
    string Severity,
    double Urgency,
    double Relevance,
    double Confidence,
    IReadOnlyList<string> TargetRefs,
    TimeSpan Lifetime,
    IReadOnlyList<string>? PolicyRefs = null);

/// <summary>
/// Where a signal goes, and what that costs the work already in progress.
/// </summary>
/// <remarks>
/// Carries no permission of any kind, deliberately. Routing decides order and attention; whether
/// anything may then be done goes through the cycle exactly as it would without the signal.
/// </remarks>
public sealed record RouteDecision(
    string SignalId,
    string Interruptibility,
    IReadOnlyList<string> ReasonCodes,
    /// <summary>The cycle this signal interrupted, parked for recovery rather than cancelled.</summary>
    string? PreservedCycleId = null);

/// <summary>The thresholds routing is judged against.</summary>
/// <remarks>
/// Interruption is a policy question, not a property of the signal: the same alert is worth
/// stopping for on a quiet evening and not worth it during an incident. Kept as configuration so
/// the answer can change without changing what a signal <i>is</i>.
/// </remarks>
public sealed record SignalPolicy(
    string InterruptAtSeverity = SignalSeverity.High,
    string EmergencyAtSeverity = SignalSeverity.Critical,
    int MaxPerWindow = 10,
    TimeSpan? Window = null)
{
    public TimeSpan DedupeWindow => Window ?? TimeSpan.FromMinutes(5);

    public static SignalPolicy Default { get; } = new();
}

public sealed class SignalException : Exception
{
    public SignalException(string message) : base(message)
    {
    }
}
