using System.Collections.Concurrent;
using System.Globalization;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Resources;

/// <summary>
/// Reads real capacity from the host, portably (RFC 033).
/// </summary>
/// <remarks>
/// Only metrics that mean the same thing on macOS, Linux and Windows are read. Anything else is
/// reported as unmeasured, which makes admission conservative — RFC 033's own instruction for a
/// missing metric, and the opposite of the tempting default where an unread number is treated as a
/// healthy one.
/// <para>
/// Reservations are held in this process and nowhere else. That is not a shortcut: a reservation
/// stands for work in flight here, and a process that died is not still using the CPU. There is
/// nothing for a restart to reconcile.
/// </para>
/// </remarks>
public sealed class SystemResourceModel : IResourceModel
{
    /// <summary>Above this, the machine is under real pressure and discretionary work gives way.</summary>
    private const double ConstrainedAt = 0.80;

    /// <summary>Above this, only essential work proceeds.</summary>
    private const double CriticalAt = 0.95;

    /// <summary>
    /// Below this much free space Aurora cannot safely do the things it must be able to do.
    /// </summary>
    /// <remarks>
    /// Derived from what Aurora writes rather than from a round number: the database and its
    /// write-ahead log, an encrypted snapshot of the same, and a backup copy beside it — with room
    /// left for none of those to be the thing that fills the disk. Half a gigabyte is generous for
    /// all three and small enough that reaching it means something is genuinely wrong.
    /// </remarks>
    private const long CriticalFreeBytes = 512L * 1024 * 1024;

    /// <summary>Below this, there is room to work and not room to be relaxed about it.</summary>
    private const long ConstrainedFreeBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// A disk this full is about to be a problem whatever the absolute figure says.
    /// </summary>
    /// <remarks>
    /// The one place the fraction still decides something. A terabyte at 99.5% has five gigabytes
    /// left, which is room — and it is also a machine filling up fast enough that discretionary
    /// work should get out of the way.
    /// </remarks>
    private const double NearlyFull = 0.99;

    /// <summary>
    /// What the disk says about whether Aurora can work.
    /// </summary>
    /// <remarks>
    /// Judged on room left rather than on proportion used. A percentage answers how much of the
    /// machine is spoken for; Aurora needs to know whether there is space to write into, and those
    /// two stop agreeing exactly when the disk is large. This was found the honest way: an
    /// instance refused every effectful action on a machine with seven gigabytes free, and it was
    /// right by its own rule and wrong about the machine.
    /// <para>
    /// Where the platform reports no free-space figure this falls back to the fraction, because a
    /// guess from the only number available beats treating an unmeasured disk as empty.
    /// </para>
    /// </remarks>
    private static string DiskStatus(long? freeBytes, double? fraction)
    {
        if (freeBytes is null)
        {
            return fraction switch
            {
                null => ResourceStatus.Unknown,
                >= CriticalAt => ResourceStatus.Critical,
                >= ConstrainedAt => ResourceStatus.Constrained,
                _ => ResourceStatus.Normal,
            };
        }

        if (freeBytes < CriticalFreeBytes)
        {
            return ResourceStatus.Critical;
        }

        return freeBytes < ConstrainedFreeBytes || fraction >= NearlyFull
            ? ResourceStatus.Constrained
            : ResourceStatus.Normal;
    }

    /// <summary>The worse of two, on the order Normal &lt; Unknown &lt; Constrained &lt; Critical.</summary>
    private static string Worse(string left, string right) =>
        Rank(left) >= Rank(right) ? left : right;

    private static int Rank(string status) => status switch
    {
        ResourceStatus.Critical => 3,
        ResourceStatus.Constrained => 2,

        // Unknown sits above Normal: RFC 033 admits conservatively on a metric nobody read.
        ResourceStatus.Unknown => 1,
        _ => 0,
    };


    private readonly ConcurrentDictionary<string, Reservation> _held = new(StringComparer.Ordinal);
    private readonly IResourceProbe _probe;
    private readonly IClock _clock;

    private double _costToday;
    private DateOnly _costDay;

    public SystemResourceModel(IResourceProbe probe, IClock clock)
    {
        _probe = probe;
        _clock = clock;
        _costDay = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
    }

    public IReadOnlyList<Reservation> Held => _held.Values.ToList();

    public Task<ResourceState> ObserveAsync(CancellationToken ct) => Task.FromResult(Observe());

    private ResourceState Observe()
    {
        ResourceReading reading = _probe.Read();
        var unmeasured = new List<string>();

        double? cpu = reading.CpuFraction;
        if (cpu is null)
        {
            unmeasured.Add("cpu");
        }

        double? memory = reading.MemoryFraction;
        if (memory is null)
        {
            unmeasured.Add("memory");
        }

        double? disk = reading.DiskFraction;
        if (disk is null)
        {
            unmeasured.Add("disk");
        }

        // No portable reading of connectivity that means anything useful — a reachable interface is
        // not a working network. Reported as unknown rather than guessed at.
        unmeasured.Add("network");

        // CPU and memory are proportions of a fixed capacity, so a fraction is the right measure
        // for them. The disk is not: see DiskStatus.
        var load = new[] { cpu, memory }.Where(v => v is not null).Select(v => v!.Value).ToList();
        var pressure = load.Count == 0 ? (double?)null : load.Max();

        var fromLoad = pressure switch
        {
            null => ResourceStatus.Unknown,
            >= CriticalAt => ResourceStatus.Critical,
            >= ConstrainedAt => ResourceStatus.Constrained,
            _ => ResourceStatus.Normal,
        };

        var status = Worse(fromLoad, DiskStatus(reading.DiskFreeBytes, disk));

        // Capacity, not comfort: what is left after the machine's own pressure and the work already
        // in flight. Unknown pressure counts as half, so an unmeasurable host is treated as busier
        // than an idle one and quieter than an overloaded one.
        var energy = Math.Clamp(1.0 - (pressure ?? 0.5), 0, 1);

        return new ResourceState(
            Guid.NewGuid().ToString("N"), Iso(_clock.UtcNow),
            Round(cpu), Round(memory), Round(disk), Core.Contracts.NetworkState.Unknown,
            QueueDepth: _held.Count, ActiveWorkers: _held.Count,
            ModelCostToday: CostToday(), RateLimitState: "UNKNOWN",
            OperationalEnergy: energy, status, unmeasured, reading.DiskFreeBytes);
    }

    public Task<AdmissionResult> AdmitAsync(
        string workRef, string workClass, double estimatedCost, ResourceBudget budget, CancellationToken ct)
    {
        if (!WorkClass.IsKnown(workClass))
        {
            throw new ResourceException($"Unknown work class '{workClass}'.");
        }

        // Rule 2: nothing is unlimited. Work that will not say what it costs cannot be admitted,
        // because an unbounded estimate is how a budget stops being a budget.
        if (estimatedCost < 0 || double.IsNaN(estimatedCost) || double.IsInfinity(estimatedCost))
        {
            return Task.FromResult(new AdmissionResult(
                Admission.Deny, "Work must carry a finite cost estimate."));
        }

        ResourceState state = Observe();
        var essential = workClass == WorkClass.Essential;

        // Rule 3: CRITICAL blocks non-essential work. It defers rather than denies — the work is
        // fine, the moment is not, and denying it would lose it.
        if (state.Status == ResourceStatus.Critical && !essential)
        {
            return Task.FromResult(new AdmissionResult(
                Admission.Defer, $"resources are {state.Status}; only essential work proceeds"));
        }

        // Rule 1: curiosity, indexing and consolidation are what gives way first, before anything
        // that a person or the system's own integrity is waiting on.
        if (state.Status == ResourceStatus.Constrained && workClass == WorkClass.Discretionary)
        {
            return Task.FromResult(new AdmissionResult(
                Admission.Defer, $"resources are {state.Status}; discretionary work waits"));
        }

        // An unreadable machine is treated as a busy one for anything optional.
        if (state.Status == ResourceStatus.Unknown && workClass == WorkClass.Discretionary)
        {
            return Task.FromResult(new AdmissionResult(
                Admission.Defer, "resource metrics are unavailable; admission is conservative"));
        }

        if (CostToday() + estimatedCost > budget.MaxCost)
        {
            // Never bill without a limit. Deferred rather than denied so a cheaper option or a
            // later window is still open to the caller.
            return Task.FromResult(new AdmissionResult(
                Admission.Defer,
                $"the {budget.TimeWindow.TotalHours:F0}h budget of {budget.MaxCost:F2} would be exceeded"));
        }

        // The reserve is what stops housekeeping from filling every slot and leaving nothing for
        // the work that cannot wait.
        var ceiling = essential
            ? budget.MaxConcurrency
            : Math.Max(0, budget.MaxConcurrency - budget.ReserveForCritical);

        if (_held.Count >= ceiling)
        {
            return Task.FromResult(new AdmissionResult(
                Admission.Defer,
                essential
                    ? $"all {budget.MaxConcurrency} slots are in use"
                    : $"{budget.ReserveForCritical} slot(s) are held for essential work"));
        }

        var reservation = new Reservation(
            Guid.NewGuid().ToString("N"), workRef, workClass, estimatedCost, Iso(_clock.UtcNow));

        _held[reservation.Id] = reservation;
        AddCost(estimatedCost);

        return Task.FromResult(new AdmissionResult(Admission.Allow, "admitted", reservation.Id));
    }

    public Task<ResourceState> ReleaseAsync(string reservationId, string outcome, CancellationToken ct)
    {
        if (!_held.TryRemove(reservationId, out _))
        {
            throw new ResourceException("Unknown reservation.");
        }

        return Task.FromResult(Observe());
    }

    // ---- metrics ----

    private double CostToday()
    {
        var today = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);
        if (today != _costDay)
        {
            _costDay = today;
            _costToday = 0;
        }

        return _costToday;
    }

    private void AddCost(double cost)
    {
        _costToday = CostToday() + cost;
    }

    private static double? Round(double? value) =>
        value is null ? null : Math.Round(value.Value, 4);

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
