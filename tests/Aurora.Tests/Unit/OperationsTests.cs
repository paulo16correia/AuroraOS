using System.Globalization;
using Aurora.Adapters.Cognition;
using Aurora.Adapters.Events;
using Aurora.Adapters.Operations;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Planning;
using Aurora.Adapters.Resources;
using Aurora.Adapters.Scheduling;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Operations (RFC 12): can this build serve traffic, and can its clock be trusted.
/// </summary>
public sealed class OperationsTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static SqliteAuditStore Audit(SqliteTestDb db, IClock clock) =>
        new(db.Factory, clock, new byte[32],
            new AuditAnchorFile(Path.Combine(Path.GetTempPath(), $"a-{Guid.NewGuid():N}")));

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
            new SqliteScheduler(db.Factory, bus, new SqliteCognitiveCycle(db.Factory, clock), clock),
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
            ["database", "audit", "clock", "event-bus", "scheduler", "resources"],
            checks.Select(c => c.Component));

        Assert.All(checks, check => Assert.Equal(HealthStatus.Pass, check.Status));
        Assert.Equal(HealthStatus.Pass, HealthStatus.Worst(checks.Select(c => c.Status)));
    }

    [Fact]
    public async Task ACriticalMachineFailsHealthSoNothingSendsItTraffic()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));

        IReadOnlyList<HealthCheck> checks = await Health(
            db, clock, Audit(db, clock), new FakeResourceProbe(disk: 0.99)).ReadAsync(Ct);

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
    public async Task AFailingCheckDoesNotTakeTheOtherFiveWithIt()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));

        // An audit store whose anchor path is a directory: every call throws.
        var broken = new SqliteAuditStore(
            db.Factory, clock, new byte[32], new AuditAnchorFile(Path.GetTempPath()));

        var health = new AuroraHealthService(
            db.Factory, broken,
            new SqliteEventBus(db.Factory, new SqliteOutbox(new PermissiveEventCatalogue(), clock), clock),
            new SystemResourceModel(new FakeResourceProbe(), clock),
            new AuditClockGuard(broken, clock),
            new SqliteScheduler(
                db.Factory,
                new SqliteEventBus(db.Factory, new SqliteOutbox(new PermissiveEventCatalogue(), clock), clock),
                new SqliteCognitiveCycle(db.Factory, clock), clock),
            clock);

        IReadOnlyList<HealthCheck> checks = await health.ReadAsync(Ct);

        // The moment a health answer matters most is when something is broken, so it reports on
        // everything rather than crashing on the first thing that is.
        Assert.Equal(6, checks.Count);
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
}
