namespace Aurora.Core.Contracts;

public static class UserAvailability
{
    public const string Online = "ONLINE";
    public const string Offline = "OFFLINE";
    public const string Unknown = "UNKNOWN";
}

public static class RiskPosture
{
    public const string Normal = "NORMAL";
    public const string Elevated = "ELEVATED";
    public const string Emergency = "EMERGENCY";
}

/// <summary>What the situation suggests Aurora should do with itself right now.</summary>
public static class ResponseMode
{
    public const string Normal = "NORMAL";

    /// <summary>Work, but do not reach for the person.</summary>
    public const string SilentInternalWork = "SILENT_INTERNAL_WORK";

    /// <summary>Ask before anything that would impose.</summary>
    public const string ConfirmBeforeImposing = "CONFIRM_BEFORE_IMPOSING";

    /// <summary>Essential work only.</summary>
    public const string EssentialOnly = "ESSENTIAL_ONLY";
}

/// <summary>
/// A momentary reading of the context an action would happen in (RFC 034).
/// </summary>
/// <remarks>
/// Not consciousness — an operational reading assembled from observable sources, which expires.
/// The same action can be correct or intrusive depending on when it happens, and this is where that
/// judgement is made explicit instead of being smuggled into whatever the last message said.
/// </remarks>
public sealed record SituationAssessment(
    string Id,
    string? CycleId,
    string EvaluatedAtUtc,
    string Timezone,
    string LocalTime,
    string UserAvailability,
    bool QuietHoursActive,
    string ChannelContext,
    IReadOnlyList<string> ActiveDelegations,
    IReadOnlyList<string> CriticalSignals,
    IReadOnlyList<string> Needs,
    string? ResourceStateRef,
    string RiskPosture,
    string RecommendedResponseMode,
    IReadOnlyList<string> EvidenceRefs,
    string ExpiresAtUtc);

/// <summary>Whether an action fits the moment, and why not when it does not.</summary>
public sealed record AppropriatenessResult(bool Appropriate, string Reason);

/// <summary>What the caller knows about the moment that Aurora cannot observe for itself.</summary>
public sealed record SituationContext(
    string Timezone,
    string? CycleId = null,
    string UserAvailability = Contracts.UserAvailability.Unknown,
    string ChannelContext = "local",
    IReadOnlyList<string>? ActiveDelegations = null);

/// <summary>The hours during which Aurora does not reach for the person unaided.</summary>
public sealed record QuietHours(TimeOnly From, TimeOnly To)
{
    public static QuietHours Default { get; } = new(new TimeOnly(22, 0), new TimeOnly(8, 0));

    /// <summary>Handles a window that crosses midnight, which is the usual shape of one.</summary>
    public bool Covers(TimeOnly local) =>
        From <= To ? local >= From && local < To : local >= From || local < To;
}

public sealed class SituationException : Exception
{
    public SituationException(string message) : base(message)
    {
    }
}
