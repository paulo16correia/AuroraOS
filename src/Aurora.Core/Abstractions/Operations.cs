using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// Reports whether Aurora is working, component by component.
/// </summary>
/// <remarks>
/// Answerable by a process that has just started and holds nothing but a socket, so it checks
/// reachable facts — can the database be read, does the audit chain verify, is the clock sane —
/// rather than asking components how they feel. A component that has stopped working is exactly
/// the one whose opinion is worthless.
/// </remarks>
public interface IHealthService
{
    Task<IReadOnlyList<HealthCheck>> ReadAsync(CancellationToken ct);
}

/// <summary>
/// Whether this machine's clock can be trusted for anything that expires.
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
