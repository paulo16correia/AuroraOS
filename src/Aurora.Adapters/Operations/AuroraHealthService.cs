using System.Diagnostics;
using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Operations;

/// <summary>
/// Component-by-component health.
/// </summary>
/// <remarks>
/// Every check reads a fact. Nothing here asks a component whether it thinks it is fine, because a
/// component that has stopped working is exactly the one whose opinion is worthless.
/// </remarks>
public sealed class AuroraHealthService : IHealthService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly IAuditStore _audit;
    private readonly IEventBus _bus;
    private readonly IResourceModel _resources;
    private readonly IClockGuard _clockGuard;
    private readonly IScheduler _scheduler;
    private readonly IClock _clock;

    public AuroraHealthService(
        SqliteConnectionFactory factory,
        IAuditStore audit,
        IEventBus bus,
        IResourceModel resources,
        IClockGuard clockGuard,
        IScheduler scheduler,
        IClock clock)
    {
        _factory = factory;
        _audit = audit;
        _bus = bus;
        _resources = resources;
        _clockGuard = clockGuard;
        _scheduler = scheduler;
        _clock = clock;
    }

    public async Task<IReadOnlyList<HealthCheck>> ReadAsync(CancellationToken ct) =>
    [
        await Timed("database", ["sqlite"], DatabaseAsync, ct).ConfigureAwait(false),
        await Timed("audit", ["database"], AuditAsync, ct).ConfigureAwait(false),
        await Timed("clock", ["audit"], ClockAsync, ct).ConfigureAwait(false),
        await Timed("event-bus", ["database"], BusAsync, ct).ConfigureAwait(false),
        await Timed("scheduler", ["database", "clock"], SchedulerAsync, ct).ConfigureAwait(false),
        await Timed("resources", [], ResourcesAsync, ct).ConfigureAwait(false),
    ];

    private async Task<(string Status, string Detail)> DatabaseAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_version LIMIT 1;";

        var version = Convert.ToInt32(
            await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);

        // A build serves the schema it was written for. Running against a database it does not
        // expect is how an upgrade quietly becomes data loss.
        return version == SqliteDatabase.TargetSchemaVersion
            ? (HealthStatus.Pass, $"schema {version}")
            : (HealthStatus.Fail,
               $"schema {version}, this build expects {SqliteDatabase.TargetSchemaVersion}");
    }

    private async Task<(string Status, string Detail)> AuditAsync(CancellationToken ct)
    {
        AuditVerification verified = await _audit.VerifyChainAsync(ct).ConfigureAwait(false);

        if (!verified.Ok)
        {
            return (HealthStatus.Fail, $"chain broken at {verified.BrokenSequence}");
        }

        // A sealed break is not a failure — it was decided and recorded — but it is permanently
        // worth seeing, so it never quietly becomes PASS.
        return verified.AcknowledgedBreakAt is { } seam
            ? (HealthStatus.Warn, $"verifies from record {seam}; everything before it is sealed off")
            : (HealthStatus.Pass, "chain verifies");
    }

    private async Task<(string Status, string Detail)> ClockAsync(CancellationToken ct)
    {
        ClockVerdict verdict = await _clockGuard.CheckAsync(ct).ConfigureAwait(false);

        return verdict.Trustworthy
            ? (HealthStatus.Pass, verdict.Detail)
            : (HealthStatus.Fail, verdict.Detail);
    }

    private async Task<(string Status, string Detail)> BusAsync(CancellationToken ct)
    {
        IReadOnlyList<Delivery> dead = await _bus.DeadLettersAsync(ct).ConfigureAwait(false);

        // Nothing was lost — that is what the dead-letter queue is for — but something stopped
        // being delivered, and a queue nobody looks at is the same as no queue.
        return dead.Count == 0
            ? (HealthStatus.Pass, "no dead letters")
            : (HealthStatus.Warn, $"{dead.Count} delivery(ies) dead-lettered and unread");
    }

    private async Task<(string Status, string Detail)> SchedulerAsync(CancellationToken ct)
    {
        IReadOnlyList<Schedule> schedules = await _scheduler.ListAsync(null, ct).ConfigureAwait(false);
        var failed = schedules.Count(s => s.Status == ScheduleStatus.Failed);

        return failed == 0
            ? (HealthStatus.Pass, $"{schedules.Count(s => s.Status == ScheduleStatus.Active)} active")
            : (HealthStatus.Warn, $"{failed} schedule(s) stopped firing and need a decision");
    }

    private async Task<(string Status, string Detail)> ResourcesAsync(CancellationToken ct)
    {
        ResourceState state = await _resources.ObserveAsync(ct).ConfigureAwait(false);

        // Disk is called out separately because RFC 12 does: a full disk is the one resource
        // problem that can cost data rather than throughput.
        var detail = state.DiskPct is { } disk
            ? $"{state.Status}, disk {disk * 100:F0}%"
            : $"{state.Status}, disk not measurable";

        return state.Status switch
        {
            ResourceStatus.Normal => (HealthStatus.Pass, detail),
            ResourceStatus.Critical => (HealthStatus.Fail, detail),
            _ => (HealthStatus.Warn, detail),
        };
    }

    /// <summary>
    /// Runs a check, times it, and turns a thrown exception into a FAIL rather than a 500.
    /// </summary>
    /// <remarks>
    /// A health endpoint that crashes when a component is broken reports nothing about the other
    /// five, which is the moment its answer matters most.
    /// </remarks>
    private async Task<HealthCheck> Timed(
        string component, string[] dependencies,
        Func<CancellationToken, Task<(string Status, string Detail)>> check, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        string status;
        string detail;

        try
        {
            (status, detail) = await check(ct).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            status = HealthStatus.Fail;

            // The type, not the message: a message can carry a path, a query or a value, and this
            // is the surface most likely to be read by something that should not see any of those.
            detail = $"check threw {failure.GetType().Name}";
        }

        return new HealthCheck(
            component, status,
            _clock.UtcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            dependencies, detail);
    }
}
