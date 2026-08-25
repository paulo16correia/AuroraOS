using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// Reports whether Aurora is working, component by component (RFC 12).
/// </summary>
/// <remarks>
/// Rule 2 requires a release to pass checks <i>before receiving traffic</i>, so this has to be
/// answerable by a process that has just started and holds nothing but a socket. It therefore
/// checks reachable facts — can the database be read, does the audit chain verify, is the clock
/// sane — rather than asking components how they feel.
/// </remarks>
public interface IHealthService
{
    Task<IReadOnlyList<HealthCheck>> ReadAsync(CancellationToken ct);
}

/// <summary>
/// Whether this machine's clock can be trusted for anything that expires (RFC 12 limit case).
/// </summary>
/// <remarks>
/// Approvals expire, consent sessions expire, signals expire, schedules fire. Every one of those is
/// a promise about time, and a clock that is wrong turns them into something else — an approval
/// that should have lapsed still standing, or a schedule that fires a year of occurrences at once.
/// <para>
/// The check needs no network. The audit log is append-only and monotonic, so a clock reading
/// earlier than the newest audit record is a clock that has gone backwards, and time does not do
/// that.
/// </para>
/// </remarks>
public interface IClockGuard
{
    Task<ClockVerdict> CheckAsync(CancellationToken ct);
}

public sealed record ClockVerdict(bool Trustworthy, string Detail, TimeSpan? Drift = null);
