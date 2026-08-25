using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Scheduling;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Scheduling;

/// <summary>
/// The Scheduler (RFC 026): rhythm, and deliberately no authority.
/// </summary>
/// <remarks>
/// Nothing here can call a capability. A tick turns elapsed time into due runs and events, and
/// stops. Whether any of them actually happens is decided afterwards, by the cognitive cycle,
/// against the same policy and approval checks as a request a person typed — because a timer that
/// could act would be a way around every one of them.
/// </remarks>
public sealed class SqliteScheduler : IScheduler
{
    /// <summary>How many missed occurrences a single tick will walk before it gives up counting.</summary>
    /// <remarks>
    /// A machine off for a year on a per-minute schedule has half a million missed occurrences, and
    /// writing half a million rows to record that is its own kind of failure. Past the cap the
    /// schedule is marked FAILED with the reason, which is a thing a person can see and fix.
    /// </remarks>
    private const int MaxMissedPerTick = 500;

    private readonly SqliteConnectionFactory _factory;
    private readonly IEventBus _bus;
    private readonly ICognitiveCycle _cycles;
    private readonly IClock _clock;

    public SqliteScheduler(
        SqliteConnectionFactory factory, IEventBus bus, ICognitiveCycle cycles, IClock clock)
    {
        _factory = factory;
        _bus = bus;
        _cycles = cycles;
        _clock = clock;
    }

    public async Task<Schedule> CreateAsync(
        ScheduleRequest request, string? approvalRef, CancellationToken ct)
    {
        if (!ScheduleTrigger.IsKnown(request.Trigger))
        {
            throw new SchedulingException($"Unknown trigger '{request.Trigger}'.");
        }

        if (!ScheduleTarget.IsKnown(request.Target))
        {
            throw new SchedulingException($"Unknown target '{request.Target}'.");
        }

        if (!MissedRunPolicy.IsKnown(request.MissedRunPolicy)
            || !QuietHoursPolicy.IsKnown(request.QuietHoursPolicy))
        {
            throw new SchedulingException("Unknown missed-run or quiet-hours policy.");
        }

        // Rule 1: the zone is mandatory and is never assumed. A schedule whose zone this machine
        // cannot resolve is a schedule that would fire at the wrong time, silently.
        TimeZoneInfo zone = ResolveZone(request.Timezone);

        // Rule 3: scheduling something that reaches outside Aurora is itself a decision the person
        // has to make. The per-occurrence checks still apply on top of this one.
        if (request.ReachesOutsideAurora && string.IsNullOrWhiteSpace(approvalRef))
        {
            throw new SchedulingException(
                "A routine that reaches outside Aurora needs an approval before it can be scheduled.");
        }

        var schedule = new Schedule(
            Guid.NewGuid().ToString("N"), request.OwnerId, request.Title, request.Trigger,
            request.Timezone, request.Expression, NextRunAtUtc: null, LastRunAtUtc: null,
            request.Target, request.PayloadRef, Enabled: true,
            request.QuietHoursPolicy, request.MissedRunPolicy, ScheduleStatus.Active);

        DateTimeOffset? first = NextOccurrence(schedule, zone, _clock.UtcNow);
        if (first is null)
        {
            throw new SchedulingException(
                $"'{request.Expression}' produces no occurrence, so there is nothing to schedule.");
        }

        schedule = schedule with { NextRunAtUtc = Iso(first.Value) };

        await ExecuteAsync("""
            INSERT INTO schedule
                (id, owner_id, title, trigger_kind, timezone, expression, next_run_at_utc,
                 last_run_at_utc, target, payload_ref, approval_ref, enabled, quiet_hours_policy,
                 missed_run_policy, status, disabled_reason)
            VALUES (@id, @owner, @title, @trigger, @tz, @expr, @next, NULL, @target, @payload,
                    @approval, 1, @quiet, @missed, @status, NULL);
            """, ct,
            ("@id", schedule.Id), ("@owner", schedule.OwnerId), ("@title", schedule.Title),
            ("@trigger", schedule.Trigger), ("@tz", schedule.Timezone),
            ("@expr", schedule.Expression), ("@next", schedule.NextRunAtUtc!),
            ("@target", schedule.Target),
            ("@payload", (object?)schedule.PayloadRef ?? DBNull.Value),
            ("@approval", (object?)approvalRef ?? DBNull.Value),
            ("@quiet", schedule.QuietHoursPolicy), ("@missed", schedule.MissedRunPolicy),
            ("@status", schedule.Status)).ConfigureAwait(false);

        return schedule;
    }

    public async Task<IReadOnlyList<ScheduleRun>> TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        var due = new List<ScheduleRun>();

        foreach (Schedule schedule in await ActiveDueAsync(now, ct).ConfigureAwait(false))
        {
            TimeZoneInfo zone;
            try
            {
                zone = ResolveZone(schedule.Timezone);
            }
            catch (SchedulingException failure)
            {
                // Rule: an invalid zone disables the schedule and notifies. It does not fall back
                // to UTC — running at the wrong hour without saying so is worse than not running.
                await DisableAsync(schedule, ScheduleStatus.Failed, failure.Message, ct).ConfigureAwait(false);
                continue;
            }

            IReadOnlyList<ScheduleRun>? produced =
                await AdvanceAsync(schedule, zone, now, ct).ConfigureAwait(false);

            if (produced is not null)
            {
                due.AddRange(produced);
            }
        }

        return due;
    }

    /// <summary>
    /// Walks one schedule from where it was up to <paramref name="now"/>.
    /// </summary>
    /// <remarks>
    /// Everything that came due while Aurora was not running is recorded, and at most one of them
    /// is offered to run. The alternative — firing each one on wake-up — is the avalanche RFC 026
    /// forbids by name.
    /// </remarks>
    private async Task<IReadOnlyList<ScheduleRun>?> AdvanceAsync(
        Schedule schedule, TimeZoneInfo zone, DateTimeOffset now, CancellationToken ct)
    {
        var elapsed = new List<DateTimeOffset>();
        DateTimeOffset cursor = Parse(schedule.NextRunAtUtc!);

        while (cursor <= now && elapsed.Count <= MaxMissedPerTick)
        {
            elapsed.Add(cursor);

            DateTimeOffset? following = NextOccurrence(schedule, zone, cursor);
            if (following is null)
            {
                break;
            }

            cursor = following.Value;
        }

        if (elapsed.Count > MaxMissedPerTick)
        {
            await DisableAsync(
                schedule, ScheduleStatus.Failed,
                $"more than {MaxMissedPerTick} occurrences came due while Aurora was not running; "
                + "the rule needs a decision rather than a catch-up", ct).ConfigureAwait(false);
            return null;
        }

        if (elapsed.Count == 0)
        {
            return [];
        }

        // ONCE has no successor: after its single occurrence the schedule is finished, not idle.
        DateTimeOffset? next = schedule.Trigger == ScheduleTrigger.Once
            ? null
            : NextOccurrence(schedule, zone, elapsed[^1]);

        // Only the last occurrence is a candidate to run. Every earlier one is recorded as missed,
        // so the gap is visible instead of silently absent.
        var missed = elapsed[..^1];
        DateTimeOffset candidate = elapsed[^1];

        var runs = new List<ScheduleRun>();

        foreach (DateTimeOffset at in missed)
        {
            ScheduleRun? recorded = await RecordAsync(
                schedule, zone, at, ScheduleRunStatus.Missed, ct).ConfigureAwait(false);

            if (recorded is not null)
            {
                runs.Add(recorded);
            }
        }

        var catchingUp = missed.Count > 0;
        var status = (catchingUp, schedule.MissedRunPolicy) switch
        {
            // Nothing was missed: this occurrence is simply due now.
            (false, _) => ScheduleRunStatus.Due,

            // Catching up. Only RUN_ONCE offers the most recent one; SKIP drops it, and ASK records
            // it and leaves the decision with the person rather than taking it for them.
            (true, MissedRunPolicy.RunOnce) => ScheduleRunStatus.Due,
            (true, MissedRunPolicy.Skip) => ScheduleRunStatus.Skipped,
            _ => ScheduleRunStatus.Missed,
        };

        ScheduleRun? current = await RecordAsync(schedule, zone, candidate, status, ct).ConfigureAwait(false);
        if (current is not null)
        {
            runs.Add(current);
        }

        await ExecuteAsync("""
            UPDATE schedule
               SET next_run_at_utc = @next, last_run_at_utc = @last,
                   status = @status, enabled = @enabled
             WHERE id = @id;
            """, ct,
            ("@next", (object?)(next is null ? null : Iso(next.Value)) ?? DBNull.Value),
            ("@last", Iso(candidate)),
            ("@status", next is null ? ScheduleStatus.Expired : ScheduleStatus.Active),
            ("@enabled", next is null ? 0 : 1),
            ("@id", schedule.Id)).ConfigureAwait(false);

        if (catchingUp && schedule.MissedRunPolicy == MissedRunPolicy.Ask)
        {
            await PublishAsync(
                EventCatalogue.ScheduleRunsMissed, schedule,
                $$"""{"missed":{{missed.Count}},"policy":"ASK"}""", ct).ConfigureAwait(false);
        }

        return runs;
    }

    /// <summary>
    /// Writes one occurrence, or nothing if that occurrence is already on record.
    /// </summary>
    /// <remarks>
    /// The unique key is the schedule plus the local wall time. When the clock goes back in autumn
    /// and 01:30 happens twice, both passes produce the same key and the second one is a no-op —
    /// which is the "at most once" RFC 026 asks for, enforced by the database rather than by
    /// remembering to check.
    /// </remarks>
    private async Task<ScheduleRun?> RecordAsync(
        Schedule schedule, TimeZoneInfo zone, DateTimeOffset dueAt, string status, CancellationToken ct)
    {
        DateTime local = TimeZoneInfo.ConvertTime(dueAt, zone).DateTime;
        var key = $"schedule:{schedule.Id}:{local:yyyy-MM-ddTHH:mm}";

        var run = new ScheduleRun(
            Guid.NewGuid().ToString("N"), schedule.Id, Iso(dueAt), StartedAtUtc: null,
            FinishedAtUtc: null, status, CycleId: null, ResultRef: null, key);

        var inserted = await ExecuteAsync("""
            INSERT OR IGNORE INTO schedule_run
                (id, schedule_id, due_at_utc, started_at_utc, finished_at_utc, status,
                 cycle_id, result_ref, idempotency_key)
            VALUES (@id, @s, @due, NULL, @finished, @status, NULL, NULL, @key);
            """, ct,
            ("@id", run.Id), ("@s", schedule.Id), ("@due", run.DueAtUtc),
            ("@finished", (object?)(ScheduleRunStatus.IsTerminal(status) ? Iso(_clock.UtcNow) : null) ?? DBNull.Value),
            ("@status", status), ("@key", key)).ConfigureAwait(false);

        if (inserted == 0)
        {
            return null;
        }

        if (status == ScheduleRunStatus.Due)
        {
            // The due run is announced as a fact, not handed to an executor. What answers it is the
            // cognitive cycle, with everything that entails.
            await PublishAsync(
                EventCatalogue.JobDue, schedule,
                $$"""{"run_id":"{{run.Id}}","target":"{{schedule.Target}}"}""", ct).ConfigureAwait(false);
        }

        return run with
        {
            FinishedAtUtc = ScheduleRunStatus.IsTerminal(status) ? Iso(_clock.UtcNow) : null,
        };
    }

    public async Task<Schedule> PauseAsync(string scheduleId, string actor, CancellationToken ct) =>
        await SetStateAsync(
            scheduleId, ScheduleStatus.Paused, enabled: false, $"paused by {actor}", ct)
            .ConfigureAwait(false);

    public async Task<Schedule> ResumeAsync(string scheduleId, string actor, CancellationToken ct)
    {
        Schedule schedule = await RequireAsync(scheduleId, ct).ConfigureAwait(false);

        if (schedule.Status == ScheduleStatus.Expired)
        {
            throw new SchedulingException("A schedule that has ended is not resumed; make a new one.");
        }

        TimeZoneInfo zone = ResolveZone(schedule.Timezone);

        // Resume from now, not from where it stopped. Otherwise resuming replays the whole pause as
        // a backlog, which is the avalanche again wearing a different hat.
        DateTimeOffset? next = NextOccurrence(schedule, zone, _clock.UtcNow);
        if (next is null)
        {
            throw new SchedulingException("The schedule has no further occurrence to resume to.");
        }

        await ExecuteAsync("""
            UPDATE schedule
               SET status = @status, enabled = 1, disabled_reason = NULL, next_run_at_utc = @next
             WHERE id = @id;
            """, ct,
            ("@status", ScheduleStatus.Active), ("@next", Iso(next.Value)), ("@id", scheduleId))
            .ConfigureAwait(false);

        return schedule with
        {
            Status = ScheduleStatus.Active,
            Enabled = true,
            DisabledReason = null,
            NextRunAtUtc = Iso(next.Value),
        };
    }

    /// <summary>
    /// Ends a schedule. The row stays, and so does every run it produced.
    /// </summary>
    /// <remarks>
    /// Rule 4 says deleting prevents future occurrences and does not delete past audits, so this is
    /// not a DELETE. RFC 026 freezes the status set at ACTIVE|PAUSED|EXPIRED|FAILED, which has no
    /// value for "the person ended it" — so it lands in EXPIRED and the reason says who did it.
    /// </remarks>
    public Task<Schedule> DeleteAsync(string scheduleId, string actor, CancellationToken ct) =>
        SetStateAsync(scheduleId, ScheduleStatus.Expired, enabled: false, $"deleted by {actor}", ct);

    public async Task<ScheduleRun> StartAsync(string runId, string cycleId, CancellationToken ct)
    {
        ScheduleRun run = await RequireRunAsync(runId, ct).ConfigureAwait(false);

        if (run.Status != ScheduleRunStatus.Due)
        {
            throw new SchedulingException($"Only a DUE run is started; this one is {run.Status}.");
        }

        var startedAt = Iso(_clock.UtcNow);
        await ExecuteAsync(
            "UPDATE schedule_run SET status = @s, started_at_utc = @at, cycle_id = @c WHERE id = @id;",
            ct,
            ("@s", ScheduleRunStatus.Started), ("@at", startedAt), ("@c", cycleId), ("@id", runId))
            .ConfigureAwait(false);

        return run with { Status = ScheduleRunStatus.Started, StartedAtUtc = startedAt, CycleId = cycleId };
    }

    public async Task<ScheduleRun> FinishAsync(
        string runId, string status, string? resultRef, CancellationToken ct)
    {
        ScheduleRun run = await RequireRunAsync(runId, ct).ConfigureAwait(false);

        if (!ScheduleRunStatus.IsTerminal(status))
        {
            throw new SchedulingException($"'{status}' is not an ending.");
        }

        if (ScheduleRunStatus.IsTerminal(run.Status))
        {
            throw new SchedulingException($"This run already ended as {run.Status}.");
        }

        var finishedAt = Iso(_clock.UtcNow);
        await ExecuteAsync(
            "UPDATE schedule_run SET status = @s, finished_at_utc = @at, result_ref = @r WHERE id = @id;",
            ct,
            ("@s", status), ("@at", finishedAt), ("@r", (object?)resultRef ?? DBNull.Value),
            ("@id", runId)).ConfigureAwait(false);

        return run with { Status = status, FinishedAtUtc = finishedAt, ResultRef = resultRef };
    }

    /// <summary>
    /// Settles a run that a crash left in flight, by reading the cycle it started.
    /// </summary>
    /// <remarks>
    /// A run whose cycle completed is a success; one whose cycle failed is a failure; one with no
    /// cycle at all never began. What is never done here is assume: a run that started a cycle
    /// still running is left alone, because guessing at it would be inventing an outcome.
    /// </remarks>
    public async Task<ScheduleRun> ReconcileAsync(string runId, CancellationToken ct)
    {
        ScheduleRun run = await RequireRunAsync(runId, ct).ConfigureAwait(false);

        if (run.Status != ScheduleRunStatus.Started)
        {
            return run;
        }

        if (run.CycleId is null)
        {
            return await FinishAsync(
                runId, ScheduleRunStatus.Failed, "started without a cycle", ct).ConfigureAwait(false);
        }

        CognitiveCycle? cycle = await _cycles.GetAsync(run.CycleId, ct).ConfigureAwait(false);

        return cycle?.Status switch
        {
            CycleStatus.Completed => await FinishAsync(
                runId, ScheduleRunStatus.Succeeded, run.CycleId, ct).ConfigureAwait(false),

            CycleStatus.Failed or CycleStatus.Cancelled => await FinishAsync(
                runId, ScheduleRunStatus.Failed, run.CycleId, ct).ConfigureAwait(false),

            null => await FinishAsync(
                runId, ScheduleRunStatus.Failed, "the cycle it started is not on record", ct)
                .ConfigureAwait(false),

            // Still running or waiting. Not finished, and not ours to call.
            _ => run,
        };
    }

    // ---- reads ----

    private const string ScheduleSelect = """
        SELECT id, owner_id, title, trigger_kind, timezone, expression, next_run_at_utc,
               last_run_at_utc, target, payload_ref, enabled, quiet_hours_policy,
               missed_run_policy, status, disabled_reason
          FROM schedule
        """;

    public async Task<Schedule?> GetAsync(string scheduleId, CancellationToken ct)
    {
        IReadOnlyList<Schedule> found = await ReadSchedulesAsync(
            $"{ScheduleSelect} WHERE id = @id;", ct, ("@id", scheduleId)).ConfigureAwait(false);

        return found.Count == 0 ? null : found[0];
    }

    public Task<IReadOnlyList<Schedule>> ListAsync(string? ownerId, CancellationToken ct) =>
        ownerId is null
            ? ReadSchedulesAsync($"{ScheduleSelect} ORDER BY title;", ct)
            : ReadSchedulesAsync(
                $"{ScheduleSelect} WHERE owner_id = @o ORDER BY title;", ct, ("@o", ownerId));

    private Task<IReadOnlyList<Schedule>> ActiveDueAsync(DateTimeOffset now, CancellationToken ct) =>
        ReadSchedulesAsync($"""
            {ScheduleSelect}
             WHERE status = @active AND enabled = 1
               AND next_run_at_utc IS NOT NULL AND next_run_at_utc <= @now
             ORDER BY next_run_at_utc;
            """, ct, ("@active", ScheduleStatus.Active), ("@now", Iso(now)));

    public async Task<IReadOnlyList<ScheduleRun>> RunsAsync(string scheduleId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, schedule_id, due_at_utc, started_at_utc, finished_at_utc, status,
                   cycle_id, result_ref, idempotency_key
              FROM schedule_run WHERE schedule_id = @s ORDER BY due_at_utc;
            """;
        command.Parameters.AddWithValue("@s", scheduleId);

        var runs = new List<ScheduleRun>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            runs.Add(ReadRun(reader));
        }

        return runs;
    }

    // ---- occurrence arithmetic ----

    /// <summary>
    /// The next moment this schedule comes due, strictly after <paramref name="after"/>.
    /// </summary>
    /// <remarks>
    /// Computed in the schedule's own wall-clock time and then converted, which is the only way
    /// "every day at 09:00" survives a DST boundary meaning 09:00 rather than a fixed offset.
    /// </remarks>
    private static DateTimeOffset? NextOccurrence(Schedule schedule, TimeZoneInfo zone, DateTimeOffset after)
    {
        switch (schedule.Trigger)
        {
            case ScheduleTrigger.Once:
            {
                if (!DateTimeOffset.TryParse(
                        schedule.Expression, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out DateTimeOffset at))
                {
                    throw new SchedulingException(
                        $"A ONCE schedule needs an instant; '{schedule.Expression}' is not one.");
                }

                return at > after ? at : null;
            }

            case ScheduleTrigger.Interval:
            {
                if (!TimeSpan.TryParse(schedule.Expression, CultureInfo.InvariantCulture, out TimeSpan every)
                    || every <= TimeSpan.Zero)
                {
                    throw new SchedulingException(
                        $"An INTERVAL schedule needs a positive duration; '{schedule.Expression}' is not one.");
                }

                DateTimeOffset from = schedule.LastRunAtUtc is { } last ? Parse(last) : after;
                DateTimeOffset next = from + every;

                // Catch up in whole intervals rather than landing off-beat after a long pause.
                while (next <= after)
                {
                    next += every;
                }

                return next;
            }

            case ScheduleTrigger.Cron:
            {
                if (!CronExpression.TryParse(schedule.Expression, out CronExpression? cron, out var error))
                {
                    throw new SchedulingException(error ?? "The cron expression is not valid.");
                }

                DateTime localAfter = TimeZoneInfo.ConvertTime(after, zone).DateTime;
                DateTime? candidate = localAfter;

                // Walk forward until a wall time that actually exists. The hour skipped in spring
                // is not a time anything can happen at, so the occurrence moves to the next match
                // rather than being invented at an offset nobody asked for.
                while ((candidate = cron!.NextLocal(candidate.Value)) is { } local)
                {
                    if (zone.IsInvalidTime(local))
                    {
                        continue;
                    }

                    // An ambiguous wall time happens twice. Take the earlier instant — the larger
                    // offset, before the clocks went back. The occurrence key is the wall time, so
                    // the second pass is recognised as the same occurrence rather than a new one.
                    TimeSpan offset = zone.IsAmbiguousTime(local)
                        ? zone.GetAmbiguousTimeOffsets(local).Max()
                        : zone.GetUtcOffset(local);

                    return new DateTimeOffset(local, offset);
                }

                return null;
            }

            default:
                // EVENT_CONDITION is not time-driven: it is woken by a matching event, not by a
                // tick. Recorded as having no next occurrence rather than pretended to be a timer.
                return null;
        }
    }

    private static TimeZoneInfo ResolveZone(string timezone)
    {
        if (string.IsNullOrWhiteSpace(timezone))
        {
            throw new SchedulingException("A schedule needs a time zone; UTC is not assumed.");
        }

        try
        {
            // IANA ids resolve on every platform .NET supports, so a schedule written on macOS
            // means the same thing on Windows.
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (Exception found) when (found is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new SchedulingException($"'{timezone}' is not a time zone this machine knows.");
        }
    }

    // ---- plumbing ----

    private async Task<Schedule> SetStateAsync(
        string scheduleId, string status, bool enabled, string reason, CancellationToken ct)
    {
        Schedule schedule = await RequireAsync(scheduleId, ct).ConfigureAwait(false);

        await ExecuteAsync("""
            UPDATE schedule
               SET status = @status, enabled = @enabled, disabled_reason = @reason,
                   next_run_at_utc = NULL
             WHERE id = @id;
            """, ct,
            ("@status", status), ("@enabled", enabled ? 1 : 0), ("@reason", reason), ("@id", scheduleId))
            .ConfigureAwait(false);

        return schedule with
        {
            Status = status, Enabled = enabled, DisabledReason = reason, NextRunAtUtc = null,
        };
    }

    private async Task DisableAsync(
        Schedule schedule, string status, string reason, CancellationToken ct)
    {
        await SetStateAsync(schedule.Id, status, enabled: false, reason, ct).ConfigureAwait(false);

        // Notified, not just recorded: a schedule that quietly stops firing is the failure people
        // find out about weeks later.
        await PublishAsync(
            EventCatalogue.ScheduleDisabled, schedule,
            $$"""{"status":"{{status}}","reason":{{Quote(reason)}}}""", ct).ConfigureAwait(false);
    }

    private Task PublishAsync(string type, Schedule schedule, string payloadJson, CancellationToken ct) =>
        _bus.PublishAsync(
            new OutboxWrite(
                type, 1, EventCatalogue.Producers.Scheduler, Guid.NewGuid().ToString("N"), Sensitivity.Private,
                AggregateRef: $"schedule/{schedule.Id}", PayloadJson: payloadJson),
            ct);

    private async Task<Schedule> RequireAsync(string scheduleId, CancellationToken ct) =>
        await GetAsync(scheduleId, ct).ConfigureAwait(false)
        ?? throw new SchedulingException("Unknown schedule.");

    private async Task<ScheduleRun> RequireRunAsync(string runId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, schedule_id, due_at_utc, started_at_utc, finished_at_utc, status,
                   cycle_id, result_ref, idempotency_key
              FROM schedule_run WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@id", runId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? ReadRun(reader)
            : throw new SchedulingException("Unknown schedule run.");
    }

    private async Task<IReadOnlyList<Schedule>> ReadSchedulesAsync(
        string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((var name, var value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var schedules = new List<Schedule>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            schedules.Add(new Schedule(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetInt32(10) == 1, reader.GetString(11), reader.GetString(12),
                reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14)));
        }

        return schedules;
    }

    private static ScheduleRun ReadRun(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8));

    private async Task<int> ExecuteAsync(
        string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((var name, var value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string Quote(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);

    /// <summary>
    /// Formats an instant for storage, always in UTC.
    /// </summary>
    /// <remarks>
    /// Normalising the offset away is not cosmetic. Due times are compared with <c>&lt;=</c> in SQL,
    /// where they are text: <c>01:30+01:00</c> sorts after <c>00:30+00:00</c> even though it is the
    /// earlier instant. Stored with a mixed offset, a schedule in any zone ahead of UTC would
    /// simply never come due.
    /// </remarks>
    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
