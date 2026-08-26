namespace Aurora.Core.Contracts;

/// <summary>How bad it is (RFC 09).</summary>
/// <remarks>
/// Only <see cref="High"/> and <see cref="Critical"/> trigger rule 5's three actions. The lower
/// three exist so that something noticed can be recorded without revoking anything — a log nobody
/// can write to below the alarm threshold is a log that gets written somewhere else instead.
/// </remarks>
public static class SecuritySeverity
{
    public const string Info = "INFO";
    public const string Low = "LOW";
    public const string Medium = "MEDIUM";
    public const string High = "HIGH";
    public const string Critical = "CRITICAL";

    /// <summary>Whether this severity is one rule 5 calls a high risk incident.</summary>
    public static bool IsHighRisk(string severity) => severity is High or Critical;
}

/// <summary>What kind of thing happened. Named once so a typo cannot invent a category.</summary>
public static class SecurityEventType
{
    /// <summary>The audit chain did not verify.</summary>
    public const string AuditChainBroken = "AUDIT_CHAIN_BROKEN";

    /// <summary>A credential-shaped value appeared where one should never be.</summary>
    public const string SecretExposed = "SECRET_EXPOSED";

    /// <summary>A plugin or tool did something its declaration did not cover.</summary>
    public const string UndeclaredBehaviour = "UNDECLARED_BEHAVIOUR";

    /// <summary>Repeated refusals against one credential or principal.</summary>
    public const string AuthenticationAbuse = "AUTHENTICATION_ABUSE";

    /// <summary>Something asked for authority it was never granted.</summary>
    public const string PrivilegeEscalation = "PRIVILEGE_ESCALATION";

    /// <summary>The host clock moved in a way that invalidates anything that expires.</summary>
    public const string ClockTampering = "CLOCK_TAMPERING";
}

public static class IncidentStatus
{
    public const string Open = "OPEN";

    /// <summary>Contained: the affected capacity is revoked and it cannot continue.</summary>
    public const string Contained = "CONTAINED";

    public const string Resolved = "RESOLVED";
}

/// <summary>
/// Something that happened and that security cares about (RFC 09).
/// </summary>
/// <param name="ResourceRef">
/// What was affected, and the reason containment can be targeted rather than total: a tool id, a
/// plugin id, or empty when the answer is "this machine".
/// </param>
/// <param name="EvidenceRef">
/// Where the proof is — an audit sequence, a cycle id, a dead letter. Rule 5 requires evidence to be
/// preserved, and evidence Aurora only holds in a log line it may rotate away is not preserved.
/// </param>
public sealed record SecurityEvent(
    string Id,
    string Severity,
    string Type,
    string CorrelationId,
    string ActorRef,
    string ResourceRef,
    string? DecisionRef,
    string EvidenceRef,
    string DetectedAtUtc);

/// <summary>
/// An open question about something that went wrong, and what was done about it immediately.
/// </summary>
/// <param name="ContainmentActions">
/// Exactly what was revoked, in order, as it happened. Not a plan and not a policy: the record of
/// what Aurora did the moment it found out, so somebody arriving later can tell the difference
/// between a system that contained itself and one that only logged.
/// </param>
public sealed record Incident(
    string Id,
    SecurityEvent Event,
    string Status,
    IReadOnlyList<string> ContainmentActions,
    string OpenedAtUtc,
    string? ContainedAtUtc,
    string? ResolvedAtUtc,
    string? Resolution);

public sealed class IncidentException : Exception
{
    public IncidentException(string message) : base(message)
    {
    }
}
