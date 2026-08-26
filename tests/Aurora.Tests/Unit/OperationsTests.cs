using System.Globalization;
using Aurora.Adapters.Cognition;
using Aurora.Adapters.Events;
using Aurora.Adapters.Operations;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Plugins.Sandboxes;
using Aurora.Adapters.Planning;
using Aurora.Adapters.Resources;
using Aurora.Adapters.Scheduling;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Operations: can this build serve traffic, and can its clock be trusted (docs/adr/0045, which
/// kept the operational half of the withdrawn RFC 12).
/// </summary>
public sealed class OperationsTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static SqliteAuditStore Audit(SqliteTestDb db, IClock clock) =>
        new(db.Factory, clock, new byte[32],
            new AuditAnchorFile(TestTemp.Path("anchor")));

    private static AuditEntry Entry() =>
        new("c1", "u1", "echo.say", "hash", "completed",
            Risk: "Low", Via: "explicit", Decision: "auto_low", PolicyIds: "p");

    private static AuroraHealthService Health(
        SqliteTestDb db, TestClock clock, SqliteAuditStore audit, FakeResourceProbe? probe = null)
    {
        var bus = new SqliteEventBus(db.Factory, new SqliteOutbox(new PermissiveEventCatalogue(), clock), clock);

        return new AuroraHealthService(
            db.Factory, audit, bus,
            new SystemResourceModel(probe ?? new FakeResourceProbe(), clock),
            new AuditClockGuard(audit, clock),
            new SqliteScheduler(db.Factory, bus, new SqliteCognitiveCycle(db.Factory, clock), clock), PluginSandbox.ForThisMachine(),
            clock);
    }

    // ---- the clock, checked without a network ----

    [Fact]
    public async Task AClockWithNothingToContradictItIsAcceptedAndSaysWhy()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));

        ClockVerdict verdict = await new AuditClockGuard(Audit(db, clock), clock).CheckAsync(Ct);

        Assert.True(verdict.Trustworthy);

        // Not the same as the clock being right, and the detail does not imply otherwise.
        Assert.Contains("nothing to compare", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClockThatWentBackwardsIsNotTrusted()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));
        SqliteAuditStore audit = Audit(db, clock);

        await audit.AppendAsync(Entry(), Ct);

        // A VM resumed from a snapshot, a container on a host with a bad clock. The audit log only
        // ever moves forward, so a clock reading earlier than its newest record has gone backwards,
        // and time does not do that.
        clock.UtcNow = At("2025-06-01T09:00:00+00:00");

        ClockVerdict verdict = await new AuditClockGuard(audit, clock).CheckAsync(Ct);

        Assert.False(verdict.Trustworthy);
        Assert.Contains("expires", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASmallNegativeDeltaIsAbsorbedRatherThanTreatedAsACrisis()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));
        SqliteAuditStore audit = Audit(db, clock);

        await audit.AppendAsync(Entry(), Ct);

        // An NTP correction mid-write, a coarse timer. Not the thing being looked for.
        clock.UtcNow = At("2026-01-15T08:59:55+00:00");

        Assert.True((await new AuditClockGuard(audit, clock).CheckAsync(Ct)).Trustworthy);
    }

    // ---- health ----

    [Fact]
    public async Task AHealthyInstanceReportsEveryComponentPassing()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));

        IReadOnlyList<HealthCheck> checks = await Health(db, clock, Audit(db, clock)).ReadAsync(Ct);

        Assert.Equal(
            ["database", "audit", "clock", "event-bus", "scheduler", "resources", "plugin-sandbox"],
            checks.Select(c => c.Component));

        Assert.All(
            checks.Where(c => c.Component != "plugin-sandbox"),
            check => Assert.Equal(HealthStatus.Pass, check.Status));

        // The sandbox check reports the machine, not the code, so it is asserted against the
        // machine. A test that demanded PASS here would fail on a Linux without bubblewrap — and
        // would be right to, which is exactly why it must not be an incidental assertion inside a
        // test about something else.
        HealthCheck sandbox = checks.Single(c => c.Component == "plugin-sandbox");

        Assert.Equal(
            OperatingSystem.IsMacOS() && File.Exists("/usr/bin/sandbox-exec")
                ? HealthStatus.Pass
                : HealthStatus.Warn,
            sandbox.Status);
    }

    [Fact]
    public async Task ACriticalMachineFailsHealthSoNothingSendsItTraffic()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));

        IReadOnlyList<HealthCheck> checks = await Health(
            db, clock, Audit(db, clock), new FakeResourceProbe(disk: 0.99, diskFreeBytes: 64L * 1024 * 1024)).ReadAsync(Ct);

        HealthCheck resources = checks.Single(c => c.Component == "resources");

        Assert.Equal(HealthStatus.Fail, resources.Status);
        Assert.Contains("99%", resources.DetailSafe, StringComparison.Ordinal);
        Assert.Equal(HealthStatus.Fail, HealthStatus.Worst(checks.Select(c => c.Status)));
    }

    [Fact]
    public async Task ASealedAuditChainWarnsForeverRatherThanQuietlyPassing()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));
        SqliteAuditStore audit = Audit(db, clock);

        await audit.AppendAsync(Entry(), Ct);
        await audit.SealBreakAsync("the key was lost", "paulo", Ct);

        HealthCheck check = (await Health(db, clock, audit).ReadAsync(Ct))
            .Single(c => c.Component == "audit");

        // Decided and recorded, so not a failure — and permanently worth seeing, so not a pass.
        Assert.Equal(HealthStatus.Warn, check.Status);
        Assert.Contains("sealed off", check.DetailSafe, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailingCheckDoesNotTakeTheOthersWithIt()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));

        // An audit store whose anchor path is a directory: every call throws.
        var broken = new SqliteAuditStore(
            db.Factory, clock, new byte[32], new AuditAnchorFile(TestTemp.Path("anchor")));

        var health = new AuroraHealthService(
            db.Factory, broken,
            new SqliteEventBus(db.Factory, new SqliteOutbox(new PermissiveEventCatalogue(), clock), clock),
            new SystemResourceModel(new FakeResourceProbe(), clock),
            new AuditClockGuard(broken, clock),
            new SqliteScheduler(
                db.Factory,
                new SqliteEventBus(db.Factory, new SqliteOutbox(new PermissiveEventCatalogue(), clock), clock),
                new SqliteCognitiveCycle(db.Factory, clock), clock), PluginSandbox.ForThisMachine(),
            clock);

        IReadOnlyList<HealthCheck> checks = await health.ReadAsync(Ct);

        // The moment a health answer matters most is when something is broken, so it reports on
        // everything rather than crashing on the first thing that is.
        Assert.Equal(7, checks.Count);
        Assert.Contains(checks, c => c.Status == HealthStatus.Pass);
    }

    [Fact]
    public async Task DetailNeverCarriesAnythingBeyondCountsAndStates()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));

        IReadOnlyList<HealthCheck> checks = await Health(db, clock, Audit(db, clock)).ReadAsync(Ct);

        // detail_safe is named that way in the RFC and the name is the rule. A health endpoint is
        // the most-scraped surface a system has.
        Assert.All(checks, check =>
        {
            Assert.DoesNotContain(Path.GetTempPath(), check.DetailSafe, StringComparison.Ordinal);
            Assert.DoesNotContain("/", check.DetailSafe, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TheWorstStatusIsWhatTheSystemIs()
    {
        Assert.Equal(HealthStatus.Pass, HealthStatus.Worst([HealthStatus.Pass, HealthStatus.Pass]));
        Assert.Equal(HealthStatus.Warn, HealthStatus.Worst([HealthStatus.Pass, HealthStatus.Warn]));
        Assert.Equal(HealthStatus.Fail, HealthStatus.Worst([HealthStatus.Warn, HealthStatus.Fail]));
    }

    // ---- the disk is judged on room left, not on proportion used (docs/adr/0061) ----

    [Theory]
    // A large disk at 97% still has room to work. This is the case that started it: an instance
    // refused every effectful action on a machine with seven gigabytes free.
    [InlineData(0.97, 7L * 1024 * 1024 * 1024, ResourceStatus.Normal)]
    // The same 97% on a small disk leaves a gigabyte, which is not room to be relaxed about.
    [InlineData(0.97, 1L * 1024 * 1024 * 1024, ResourceStatus.Constrained)]
    // And below half a gigabyte Aurora cannot safely write a snapshot or a backup.
    [InlineData(0.97, 200L * 1024 * 1024, ResourceStatus.Critical)]
    // Nearly full is the one place the fraction still decides something: a terabyte at 99.5% has
    // room and is also filling fast enough that discretionary work should get out of the way.
    [InlineData(0.995, 5L * 1024 * 1024 * 1024, ResourceStatus.Constrained)]
    public async Task TheDiskIsJudgedOnRoomLeft(double fraction, long freeBytes, string expected)
    {
        var model = new SystemResourceModel(
            new FakeResourceProbe(disk: fraction, diskFreeBytes: freeBytes),
            new TestClock(At("2026-01-15T09:00:00+00:00")));

        ResourceState state = await model.ObserveAsync(Ct);

        Assert.Equal(expected, state.Status);
        Assert.Equal(freeBytes, state.DiskFreeBytes);
    }

    [Fact]
    public async Task APlatformThatReportsNoFreeSpaceFallsBackToTheFraction()
    {
        var model = new SystemResourceModel(
            new FakeResourceProbe(disk: 0.97, diskFreeBytes: null),
            new TestClock(At("2026-01-15T09:00:00+00:00")));

        ResourceState state = await model.ObserveAsync(Ct);

        // A guess from the only number available beats treating an unmeasured disk as empty — and
        // beats treating it as healthy, which is the failure the unmeasured list exists to prevent.
        Assert.Equal(ResourceStatus.Critical, state.Status);
        Assert.Null(state.DiskFreeBytes);
    }

    [Fact]
    public async Task HealthReportsBothFiguresSoTheStatusMakesSense()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));

        IReadOnlyList<HealthCheck> checks = await Health(
            db, clock, Audit(db, clock),
            new FakeResourceProbe(disk: 0.97, diskFreeBytes: 7L * 1024 * 1024 * 1024)).ReadAsync(Ct);

        HealthCheck resources = checks.Single(c => c.Component == "resources");

        // A reader given only "97%" would think a healthy instance was about to fall over.
        Assert.Equal(HealthStatus.Pass, resources.Status);
        Assert.Contains("97% used", resources.DetailSafe, StringComparison.Ordinal);
        Assert.Contains("7.0 GB free", resources.DetailSafe, StringComparison.Ordinal);
    }
}
