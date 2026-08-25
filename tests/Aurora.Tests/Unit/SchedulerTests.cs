using System.Globalization;
using Aurora.Adapters.Cognition;
using Aurora.Adapters.Events;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Scheduling;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Scheduling;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// The Scheduler (RFC 026): rhythm without authority.
/// </summary>
/// <remarks>
/// Each test is named after the rule or limit case it holds, so the RFC can be checked against
/// this file without reading the implementation.
/// </remarks>
public sealed class SchedulerTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>Lisbon, because its DST transitions are the ones the RFC's limit cases describe.</summary>
    private const string Lisbon = "Europe/Lisbon";

    private static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static (SqliteScheduler Scheduler, TestClock Clock) Build(SqliteTestDb db, DateTimeOffset now)
    {
        var clock = new TestClock(now);
        return (
            new SqliteScheduler(
                db.Factory,
                new SqliteEventBus(db.Factory, new SqliteOutbox(new PermissiveEventCatalogue(), clock), clock),
                new SqliteCognitiveCycle(db.Factory, clock),
                clock),
            clock);
    }

    private static ScheduleRequest Daily(string at = "0 9 * * *") =>
        new("morning review", "paulo", ScheduleTrigger.Cron, Lisbon, at, ScheduleTarget.CycleTemplate);

    // ---- rule 1: the time zone is mandatory and never assumed ----

    [Fact]
    public async Task ASchedulerWithNoTimeZoneIsRefusedRatherThanTreatedAsUtc()
    {
        using var db = new SqliteTestDb();
        var (scheduler, _) = Build(db, At("2026-01-01T00:00:00+00:00"));

        await Assert.ThrowsAsync<SchedulingException>(() =>
            scheduler.CreateAsync(Daily() with { Timezone = "" }, null, Ct));
    }

    [Fact]
    public async Task AZoneThisMachineDoesNotKnowIsRefusedAtCreation()
    {
        using var db = new SqliteTestDb();
        var (scheduler, _) = Build(db, At("2026-01-01T00:00:00+00:00"));

        await Assert.ThrowsAsync<SchedulingException>(() =>
            scheduler.CreateAsync(Daily() with { Timezone = "Mars/Olympus_Mons" }, null, Ct));
    }

    // ---- rule 3: scheduling is not perpetual consent ----

    [Fact]
    public async Task ARoutineThatReachesOutsideAuroraCannotBeScheduledWithoutAnApproval()
    {
        using var db = new SqliteTestDb();
        var (scheduler, _) = Build(db, At("2026-01-01T00:00:00+00:00"));

        SchedulingException refused = await Assert.ThrowsAsync<SchedulingException>(() =>
            scheduler.CreateAsync(Daily() with { ReachesOutsideAurora = true }, null, Ct));

        Assert.Contains("approval", refused.Message, StringComparison.OrdinalIgnoreCase);

        // With one, it can exist — and each occurrence is still checked when it comes due.
        Schedule created = await scheduler.CreateAsync(
            Daily() with { ReachesOutsideAurora = true }, "approval/1", Ct);

        Assert.Equal(ScheduleStatus.Active, created.Status);
    }

    // ---- rule 2: an occurrence is a due run and an event, never an execution ----

    [Fact]
    public async Task ATickProducesADueRunAndAnnouncesItWithoutRunningAnything()
    {
        using var db = new SqliteTestDb();
        var (scheduler, _) = Build(db, At("2026-01-15T07:00:00+00:00"));

        Schedule schedule = await scheduler.CreateAsync(Daily(), null, Ct);
        Assert.Equal(At("2026-01-15T09:00:00+00:00"), DateTimeOffset.Parse(schedule.NextRunAtUtc!));

        IReadOnlyList<ScheduleRun> due = await scheduler.TickAsync(At("2026-01-15T09:00:00+00:00"), Ct);

        ScheduleRun run = Assert.Single(due);
        Assert.Equal(ScheduleRunStatus.Due, run.Status);
        Assert.Null(run.StartedAtUtc);

        // Nothing ran. A run only starts when something else picks it up and carries it through
        // the cycle — which is the whole reason the Scheduler holds no capability.
        Assert.Null(run.CycleId);
    }

    [Fact]
    public async Task TheNextOccurrenceIsScheduledAfterTheOneThatCameDue()
    {
        using var db = new SqliteTestDb();
        var (scheduler, _) = Build(db, At("2026-01-15T07:00:00+00:00"));

        Schedule schedule = await scheduler.CreateAsync(Daily(), null, Ct);
        await scheduler.TickAsync(At("2026-01-15T09:00:00+00:00"), Ct);

        Schedule advanced = (await scheduler.GetAsync(schedule.Id, Ct))!;
        Assert.Equal(At("2026-01-16T09:00:00+00:00"), DateTimeOffset.Parse(advanced.NextRunAtUtc!));
    }

    // ---- limit case: the machine was off ----

    [Fact]
    public async Task OccurrencesMissedWhileAuroraWasOffAreRecordedAndOnlyOneIsOffered()
    {
        using var db = new SqliteTestDb();
        var (scheduler, _) = Build(db, At("2026-01-15T07:00:00+00:00"));

        Schedule schedule = await scheduler.CreateAsync(
            Daily() with { MissedRunPolicy = MissedRunPolicy.RunOnce }, null, Ct);

        // Four days later. The failure mode this guards against is four jobs firing at once.
        IReadOnlyList<ScheduleRun> produced = await scheduler.TickAsync(At("2026-01-19T10:00:00+00:00"), Ct);

        // The 15th through the 19th: five occurrences, four of them missed.
        Assert.Equal(5, produced.Count);
        Assert.Equal(4, produced.Count(r => r.Status == ScheduleRunStatus.Missed));
        Assert.Single(produced, r => r.Status == ScheduleRunStatus.Due);

        // The one offered is the most recent, not the oldest: catching up on this morning is
        // useful, catching up on Thursday's morning is not.
        ScheduleRun offered = produced.Single(r => r.Status == ScheduleRunStatus.Due);
        Assert.Equal(At("2026-01-19T09:00:00+00:00"), DateTimeOffset.Parse(offered.DueAtUtc));
    }

    [Fact]
    public async Task TheDefaultForMissedOccurrencesIsToRunNoneOfThem()
    {
        using var db = new SqliteTestDb();
        var (scheduler, _) = Build(db, At("2026-01-15T07:00:00+00:00"));

        // No missed-run policy stated. The default must not be an avalanche.
        Schedule schedule = await scheduler.CreateAsync(Daily(), null, Ct);
        Assert.Equal(MissedRunPolicy.Skip, schedule.MissedRunPolicy);

        IReadOnlyList<ScheduleRun> produced = await scheduler.TickAsync(At("2026-01-19T10:00:00+00:00"), Ct);

        Assert.DoesNotContain(produced, r => r.Status == ScheduleRunStatus.Due);
        Assert.NotEmpty(produced);
    }

    [Fact]
    public async Task AskLeavesTheDecisionWithThePersonRatherThanRunningAnything()
    {
        using var db = new SqliteTestDb();
        var (scheduler, _) = Build(db, At("2026-01-15T07:00:00+00:00"));

        await scheduler.CreateAsync(Daily() with { MissedRunPolicy = MissedRunPolicy.Ask }, null, Ct);

        IReadOnlyList<ScheduleRun> produced = await scheduler.TickAsync(At("2026-01-19T10:00:00+00:00"), Ct);

        Assert.DoesNotContain(produced, r => r.Status == ScheduleRunStatus.Due);
        Assert.All(produced, r => Assert.Equal(ScheduleRunStatus.Missed, r.Status));
    }

    // ---- limit case: daylight saving ----

    [Fact]
    public async Task NineOClockStaysNineOClockAcrossADaylightSavingChange()
    {
        using var db = new SqliteTestDb();

        // Lisbon goes to UTC+1 on 29 March 2026. A schedule pinned to an offset would drift to
        // 10:00 local; one pinned to wall time does not.
        var (scheduler, _) = Build(db, At("2026-03-28T07:00:00+00:00"));
        Schedule schedule = await scheduler.CreateAsync(Daily(), null, Ct);

        Assert.Equal(At("2026-03-28T09:00:00+00:00"), DateTimeOffset.Parse(schedule.NextRunAtUtc!));

        await scheduler.TickAsync(At("2026-03-28T09:00:00+00:00"), Ct);
        Schedule after = (await scheduler.GetAsync(schedule.Id, Ct))!;

        // 09:00 Lisbon on the 29th is 08:00 UTC, because the clocks went forward.
        Assert.Equal(At("2026-03-29T08:00:00+00:00"), DateTimeOffset.Parse(after.NextRunAtUtc!));
    }

    [Fact]
    public async Task AnHourThatDoesNotExistIsSkippedRatherThanInvented()
    {
        using var db = new SqliteTestDb();

        // 01:30 does not happen on 29 March 2026 in Lisbon: the clocks jump 01:00 → 02:00.
        var (scheduler, _) = Build(db, At("2026-03-28T12:00:00+00:00"));
        Schedule schedule = await scheduler.CreateAsync(Daily("30 1 * * *"), null, Ct);

        // The next 01:30 is the 30th, not a made-up instant on the 29th.
        Assert.Equal(At("2026-03-30T00:30:00+00:00"), DateTimeOffset.Parse(schedule.NextRunAtUtc!));
    }

    [Fact]
    public async Task AnHourThatHappensTwiceRunsOnce()
    {
        using var db = new SqliteTestDb();

        // Lisbon returns to UTC+0 on 25 October 2026: 01:30 local happens at 00:30 UTC and again
        // at 01:30 UTC.
        var (scheduler, _) = Build(db, At("2026-10-25T00:00:00+00:00"));
        Schedule schedule = await scheduler.CreateAsync(Daily("30 1 * * *"), null, Ct);

        await scheduler.TickAsync(At("2026-10-25T00:30:00+00:00"), Ct);
        await scheduler.TickAsync(At("2026-10-25T01:30:00+00:00"), Ct);

        IReadOnlyList<ScheduleRun> runs = await scheduler.RunsAsync(schedule.Id, Ct);

        // One occurrence, whichever way the clock went. The occurrence key is the wall time, so
        // the repeat is recognised as the same 01:30 rather than a second one.
        Assert.Single(runs, r => r.DueAtUtc.StartsWith("2026-10-25", StringComparison.Ordinal));
    }

    // ---- rule 4: the person can pause, resume and end a schedule ----

    [Fact]
    public async Task APausedScheduleStopsComingDue()
    {
        using var db = new SqliteTestDb();
        var (scheduler, _) = Build(db, At("2026-01-15T07:00:00+00:00"));

        Schedule schedule = await scheduler.CreateAsync(Daily(), null, Ct);
        await scheduler.PauseAsync(schedule.Id, "paulo", Ct);

        Assert.Empty(await scheduler.TickAsync(At("2026-01-16T10:00:00+00:00"), Ct));
    }

    [Fact]
    public async Task ResumingPicksUpFromNowRatherThanReplayingThePause()
    {
        using var db = new SqliteTestDb();
        var (scheduler, clock) = Build(db, At("2026-01-15T07:00:00+00:00"));

        Schedule schedule = await scheduler.CreateAsync(Daily(), null, Ct);
        await scheduler.PauseAsync(schedule.Id, "paulo", Ct);

        clock.UtcNow = At("2026-01-20T07:00:00+00:00");
        Schedule resumed = await scheduler.ResumeAsync(schedule.Id, "paulo", Ct);

        // Not five days of backlog: resuming a paused schedule is not a request for what it would
        // have done.
        Assert.Equal(At("2026-01-20T09:00:00+00:00"), DateTimeOffset.Parse(resumed.NextRunAtUtc!));

        IReadOnlyList<ScheduleRun> produced = await scheduler.TickAsync(At("2026-01-20T09:00:00+00:00"), Ct);
        Assert.Single(produced);
    }

    [Fact]
    public async Task DeletingStopsFutureOccurrencesAndKeepsThePastOnes()
    {
        using var db = new SqliteTestDb();
        var (scheduler, _) = Build(db, At("2026-01-15T07:00:00+00:00"));

        Schedule schedule = await scheduler.CreateAsync(Daily(), null, Ct);
        await scheduler.TickAsync(At("2026-01-15T09:00:00+00:00"), Ct);

        Schedule ended = await scheduler.DeleteAsync(schedule.Id, "paulo", Ct);

        Assert.Equal(ScheduleStatus.Expired, ended.Status);
        Assert.Contains("deleted by paulo", ended.DisabledReason);
        Assert.Empty(await scheduler.TickAsync(At("2026-01-16T10:00:00+00:00"), Ct));

        // Rule 4: deleting prevents future occurrences; it does not erase what already happened.
        Assert.NotEmpty(await scheduler.RunsAsync(schedule.Id, Ct));
    }

    // ---- reconciliation: a crash does not become a success ----

    [Fact]
    public async Task ARunLeftInFlightIsSettledFromItsCycleRatherThanAssumed()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T07:00:00+00:00"));
        var cycles = new SqliteCognitiveCycle(db.Factory, clock);
        var scheduler = new SqliteScheduler(
            db.Factory, new SqliteEventBus(db.Factory, new SqliteOutbox(new PermissiveEventCatalogue(), clock), clock), cycles, clock);

        Schedule schedule = await scheduler.CreateAsync(Daily(), null, Ct);
        ScheduleRun due = (await scheduler.TickAsync(At("2026-01-15T09:00:00+00:00"), Ct)).Single();

        CognitiveCycle cycle = await cycles.RunAsync(new CycleIngress("job", "c", null), Ct);
        await scheduler.StartAsync(due.Id, cycle.Id, Ct);

        // The cycle is still running, so the run is not ours to call either way.
        ScheduleRun stillRunning = await scheduler.ReconcileAsync(due.Id, Ct);
        Assert.Equal(ScheduleRunStatus.Started, stillRunning.Status);

        await cycles.FailAsync(cycle.Id, "the process died", Ct);

        ScheduleRun settled = await scheduler.ReconcileAsync(due.Id, Ct);
        Assert.Equal(ScheduleRunStatus.Failed, settled.Status);
    }

    // ---- the cron field parser ----

    [Theory]
    [InlineData("0 9 * * *", "2026-01-15T08:00:00", "2026-01-15T09:00:00")]
    [InlineData("*/15 * * * *", "2026-01-15T09:01:00", "2026-01-15T09:15:00")]
    [InlineData("0 0 1 * *", "2026-01-15T09:00:00", "2026-02-01T00:00:00")]
    [InlineData("0 8 * * 1", "2026-01-15T09:00:00", "2026-01-19T08:00:00")]
    [InlineData("30 6,18 * * *", "2026-01-15T07:00:00", "2026-01-15T18:30:00")]
    public void CronFindsTheNextMatchingWallTime(string expression, string after, string expected)
    {
        Assert.True(CronExpression.TryParse(expression, out CronExpression? cron, out _));

        DateTime? next = cron!.NextLocal(DateTime.Parse(after, CultureInfo.InvariantCulture));

        Assert.Equal(DateTime.Parse(expected, CultureInfo.InvariantCulture), next);
    }

    [Theory]
    [InlineData("0 9 * *")]
    [InlineData("60 9 * * *")]
    [InlineData("0 9 32 * *")]
    [InlineData("0 9 * * 8")]
    [InlineData("0 9 * * abc")]
    [InlineData("0 9-5 * * *")]
    public void AnUnparseableCronExpressionSaysWhichFieldIsWrong(string expression)
    {
        Assert.False(CronExpression.TryParse(expression, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
