using System.Globalization;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Operations;

/// <summary>
/// Checks the clock against the one monotonic thing Aurora already keeps (RFC 12).
/// </summary>
/// <remarks>
/// No NTP, no network, no trusted third party: the audit log is append-only and its timestamps only
/// ever move forward, so a clock reading earlier than the newest audit record is a clock that has
/// gone backwards. Time does not do that.
/// <para>
/// This catches the case that matters — a machine restored from a snapshot, a VM resumed, a
/// container with a bad host clock — and it deliberately does not try to catch a clock that is
/// uniformly fast or slow. That would need an external reference, and claiming to detect it without
/// one would be worse than admitting the limit.
/// </para>
/// </remarks>
public sealed class AuditClockGuard : IClockGuard
{
    /// <summary>
    /// How far behind the newest audit record the clock may read before it is not trusted.
    /// </summary>
    /// <remarks>
    /// A few seconds absorbs the ordinary sources of a small negative delta — an NTP correction
    /// mid-write, a coarse timer — without absorbing the thing being looked for, which is a jump
    /// of hours or years.
    /// </remarks>
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(30);

    private readonly IAuditStore _audit;
    private readonly IClock _clock;

    public AuditClockGuard(IAuditStore audit, IClock clock)
    {
        _audit = audit;
        _clock = clock;
    }

    public async Task<ClockVerdict> CheckAsync(CancellationToken ct)
    {
        IReadOnlyList<AuditRecordView> newest = await Newest(ct).ConfigureAwait(false);

        if (newest.Count == 0)
        {
            // Nothing has happened yet, so there is nothing the clock can contradict. Not the same
            // as the clock being right, and the detail says so rather than implying otherwise.
            return new ClockVerdict(true, "no audited action yet; there is nothing to compare against");
        }

        if (!DateTimeOffset.TryParse(
                newest[^1].CreatedAtUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTimeOffset latest))
        {
            return new ClockVerdict(false, "the newest audit record has an unreadable timestamp");
        }

        TimeSpan drift = latest - _clock.UtcNow;

        if (drift <= Tolerance)
        {
            return new ClockVerdict(true, "the clock is at or ahead of the newest audited action", drift);
        }

        return new ClockVerdict(
            false,
            $"the clock reads {drift.TotalSeconds:F0}s earlier than the newest audited action; "
            + "anything that expires cannot be judged until it is corrected",
            drift);
    }

    /// <summary>
    /// The last page of the journal.
    /// </summary>
    /// <remarks>
    /// Walked forward in pages rather than read whole: the check runs on every health poll, and a
    /// health check that reads the entire audit log is a health check that stops being cheap
    /// exactly when the log gets interesting.
    /// </remarks>
    private async Task<IReadOnlyList<AuditRecordView>> Newest(CancellationToken ct)
    {
        IReadOnlyList<AuditRecordView> page = await _audit.QueryAsync(0, 200, ct).ConfigureAwait(false);
        while (page.Count == 200)
        {
            IReadOnlyList<AuditRecordView> next =
                await _audit.QueryAsync(page[^1].Sequence, 200, ct).ConfigureAwait(false);

            if (next.Count == 0)
            {
                break;
            }

            page = next;
        }

        return page;
    }
}
