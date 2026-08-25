using System.Globalization;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Situation;

/// <summary>
/// Operational situational awareness (RFC 034).
/// </summary>
/// <remarks>
/// The same action can be correct or intrusive depending on when it happens. This is where that
/// judgement is made explicit rather than smuggled into whatever the last message said — and it is
/// a judgement that can only ever make Aurora quieter or more careful. Nothing here permits
/// anything.
/// </remarks>
public sealed class SituationService : ISituationService
{
    /// <summary>How long a reading stays usable.</summary>
    /// <remarks>
    /// Short on purpose. Rule 1 says an assessment is instantaneous and expires; the point is that
    /// nobody reuses a reading of the room taken before the room changed.
    /// </remarks>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Producers whose signals Aurora will treat as grounds for an emergency.
    /// </summary>
    /// <remarks>
    /// Rule 4: received content cannot falsely declare an emergency. A message that says URGENT is
    /// still a message; only Aurora's own observation of its own health and scheduling can raise
    /// the posture that far. External material can make Aurora more careful and no more.
    /// </remarks>
    private static readonly HashSet<string> TrustedForEmergency =
        new(StringComparer.Ordinal) { SignalKind.Health, SignalKind.Alert, SignalKind.Schedule };

    private readonly ISignalService _signals;
    private readonly INeedsService _needs;
    private readonly IResourceModel _resources;
    private readonly QuietHours _quietHours;
    private readonly IClock _clock;

    public SituationService(
        ISignalService signals, INeedsService needs, IResourceModel resources,
        QuietHours quietHours, IClock clock)
    {
        _signals = signals;
        _needs = needs;
        _resources = resources;
        _quietHours = quietHours;
        _clock = clock;
    }

    public async Task<SituationAssessment> AssessAsync(SituationContext context, CancellationToken ct)
    {
        TimeZoneInfo zone = ResolveZone(context.Timezone);
        DateTimeOffset now = _clock.UtcNow;
        DateTime local = TimeZoneInfo.ConvertTime(now, zone).DateTime;

        ResourceState resources = await _resources.ObserveAsync(ct).ConfigureAwait(false);
        IReadOnlyList<Signal> pending = await _signals.PendingAsync(ct).ConfigureAwait(false);
        IReadOnlyList<Need> needs = await _needs.RankAsync(ct).ConfigureAwait(false);

        var severe = pending
            .Where(s => SignalSeverity.Rank(s.Severity) >= SignalSeverity.Rank(SignalSeverity.High))
            .ToList();

        // Only Aurora's own observations reach EMERGENCY. A CRITICAL signal about an incoming
        // message raises alertness; it does not get to declare a crisis on the sender's say-so.
        var grounds = severe.Where(s => TrustedForEmergency.Contains(s.Kind)).ToList();

        var posture = (resources.Status, grounds.Count, severe.Count) switch
        {
            (ResourceStatus.Critical, _, _) => RiskPosture.Emergency,
            (_, > 0, _) when grounds.Any(s => s.Severity == SignalSeverity.Critical) => RiskPosture.Emergency,
            (_, > 0, _) => RiskPosture.Elevated,
            (_, _, > 0) => RiskPosture.Elevated,
            (ResourceStatus.Constrained, _, _) => RiskPosture.Elevated,
            _ => RiskPosture.Normal,
        };

        var quiet = _quietHours.Covers(TimeOnly.FromDateTime(local));

        var mode = (posture, quiet, context.UserAvailability) switch
        {
            (RiskPosture.Emergency, _, _) => ResponseMode.EssentialOnly,

            // Quiet hours change how Aurora reaches out, not whether it works.
            (_, true, _) => ResponseMode.SilentInternalWork,

            // Rule 2 in practice: not knowing where the person is means choosing the less intrusive
            // behaviour, never treating the silence as leave to go ahead.
            (_, _, UserAvailability.Unknown or UserAvailability.Offline) =>
                ResponseMode.ConfirmBeforeImposing,

            _ => ResponseMode.Normal,
        };

        return new SituationAssessment(
            Guid.NewGuid().ToString("N"), context.CycleId, Iso(now), context.Timezone,
            local.ToString("O", CultureInfo.InvariantCulture),
            context.UserAvailability, quiet, context.ChannelContext,
            context.ActiveDelegations ?? [],
            severe.Select(s => s.Id).ToList(),
            needs.Select(n => n.Id).ToList(),
            resources.Id, posture, mode,
            EvidenceRefs: [resources.Id, .. severe.Select(s => s.Id)],
            ExpiresAtUtc: Iso(now + Lifetime));
    }

    public AppropriatenessResult IsAppropriate(
        string workClass, bool imposesOnUser, SituationAssessment assessment)
    {
        // Rule 1: a stale reading is refused rather than trusted. Reusing yesterday's sense of the
        // room is worse than admitting there is no current one.
        if (Parse(assessment.ExpiresAtUtc) <= _clock.UtcNow)
        {
            return new AppropriatenessResult(false, "the assessment has expired; take a new reading");
        }

        var essential = workClass == WorkClass.Essential;

        if (assessment.RiskPosture == RiskPosture.Emergency && !essential)
        {
            return new AppropriatenessResult(false, "the posture is EMERGENCY; essential work only");
        }

        if (assessment.QuietHoursActive && imposesOnUser && !essential)
        {
            return new AppropriatenessResult(
                false, "quiet hours are active; internal work is fine, reaching for the person is not");
        }

        // Rule 2, stated plainly: being offline is not consent. It changes when and how the person
        // is reached, and never whether Aurora may act without them.
        if (imposesOnUser
            && assessment.UserAvailability is UserAvailability.Unknown or UserAvailability.Offline
            && !essential)
        {
            return new AppropriatenessResult(
                false, "the person's availability is not established; confirm before imposing");
        }

        return new AppropriatenessResult(true, "the moment fits");
    }

    private static TimeZoneInfo ResolveZone(string timezone)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (Exception unknown) when (unknown is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new SituationException($"'{timezone}' is not a time zone this machine knows.");
        }
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
