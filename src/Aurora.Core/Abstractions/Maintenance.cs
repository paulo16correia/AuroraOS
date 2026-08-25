using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// What one pass of housekeeping found and did.
/// </summary>
/// <remarks>
/// Everything counted here is bookkeeping Aurora performed on its own records. Nothing in this
/// report describes an effect outside Aurora, and that is a property of the service rather than a
/// coincidence of this run.
/// </remarks>
public sealed record MaintenanceReport(
    string AtUtc,
    int SignalsExpired,
    int NeedsDecayed,
    int ReservationsReconciled,
    int SchedulesFailed,
    IReadOnlyList<string> DueRunIds,
    IReadOnlyList<string> RankedNeedIds,
    string ResourceStatus,
    string RiskPosture,
    /// <summary>Working records removed as past retention. Never audit, memory, goals or missions.</summary>
    RetentionReport Retention,
    /// <summary>
    /// Conditions this pass did not look at. Reported rather than defaulted to zero, so an
    /// unnoticed need is visible as unmeasured instead of appearing to be absent.
    /// </summary>
    IReadOnlyList<string> Unmeasured);

/// <summary>
/// The upkeep pass (step 11 of the frozen order).
/// </summary>
/// <remarks>
/// Deliberately confined to things that cannot go wrong in an interesting way: expiring what has
/// expired, decaying what nobody acted on, reconciling what a crash left in the air, and noticing
/// what needs doing. It produces due runs and drafted needs; it never runs them. Every one of those
/// still goes through the cycle.
/// </remarks>
public interface IMaintenanceService
{
    Task<MaintenanceReport> RunAsync(SituationContext context, CancellationToken ct);
}
