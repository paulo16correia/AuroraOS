using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Retention;

/// <summary>
/// Forgets the by-products of working, and nothing else (docs/adr/0036).
/// </summary>
/// <remarks>
/// ADR 0031 recorded that cycle history grows without bound, and ADR 0033 the same for signals and
/// proposals. This is the answer, and its shape is the important part: it touches closed working
/// records only. The audit chain, memories, goals and missions are untouchable here — a system that
/// tidies away its own history on a schedule is one whose history cannot be relied on, and the
/// audit chain in particular would stop verifying if a single record vanished.
/// </remarks>
public sealed class SqliteRetentionService : IRetentionService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly IClock _clock;

    public SqliteRetentionService(SqliteConnectionFactory factory, IClock clock)
    {
        _factory = factory;
        _clock = clock;
    }

    public async Task<RetentionReport> ApplyAsync(RetentionPolicy policy, CancellationToken ct)
    {
        DateTimeOffset now = _clock.UtcNow;

        // What each expiring cycle worked in goes first — children before parents — so nothing is
        // left pointing at a cycle that has gone. Only the cycle's own scratch: the decision, the
        // action and its observation are the record of what was done and are not touched here.
        var scratch = 0;
        foreach (var statement in ScratchOfExpiringCycles)
        {
            scratch += await ExecuteAsync(
                statement, policy.Cycles, now, ct,
                ("@completed", CycleStatus.Completed), ("@failed", CycleStatus.Failed),
                ("@cancelled", CycleStatus.Cancelled)).ConfigureAwait(false);
        }

        // Stage records go next, so a cycle is never left with orphaned stages if the pass is
        // interrupted between the two statements.
        var stages = await ExecuteAsync("""
            DELETE FROM cycle_stage_record
             WHERE cycle_id IN (
               SELECT id FROM cognitive_cycle
                WHERE status IN (@completed, @failed, @cancelled)
                  AND completed_at_utc IS NOT NULL AND completed_at_utc < @before);
            """, policy.Cycles, now, ct,
            ("@completed", CycleStatus.Completed), ("@failed", CycleStatus.Failed),
            ("@cancelled", CycleStatus.Cancelled)).ConfigureAwait(false);

        // A cycle still running never ages out, however old it is: an old unfinished cycle is the
        // most interesting record in the table, not the least.
        var cycles = await ExecuteAsync("""
            DELETE FROM cognitive_cycle
             WHERE status IN (@completed, @failed, @cancelled)
               AND completed_at_utc IS NOT NULL AND completed_at_utc < @before;
            """, policy.Cycles, now, ct,
            ("@completed", CycleStatus.Completed), ("@failed", CycleStatus.Failed),
            ("@cancelled", CycleStatus.Cancelled)).ConfigureAwait(false);

        var runs = await ExecuteAsync("""
            DELETE FROM schedule_run
             WHERE status IN (@succeeded, @failed, @skipped, @missed)
               AND due_at_utc < @before;
            """, policy.ScheduleRuns, now, ct,
            ("@succeeded", ScheduleRunStatus.Succeeded), ("@failed", ScheduleRunStatus.Failed),
            ("@skipped", ScheduleRunStatus.Skipped), ("@missed", ScheduleRunStatus.Missed))
            .ConfigureAwait(false);

        var signals = await ExecuteAsync("""
            DELETE FROM signal
             WHERE status IN (@resolved, @expired, @suppressed) AND created_at_utc < @before;
            """, policy.Signals, now, ct,
            ("@resolved", SignalStatus.Resolved), ("@expired", SignalStatus.Expired),
            ("@suppressed", SignalStatus.Suppressed)).ConfigureAwait(false);

        var proposals = await ExecuteAsync("""
            DELETE FROM curiosity_proposal
             WHERE status IN (@expired, @rejected) AND detected_at_utc < @before;
            """, policy.CuriosityProposals, now, ct,
            ("@expired", CuriosityStatus.Expired), ("@rejected", CuriosityStatus.Rejected))
            .ConfigureAwait(false);

        return new RetentionReport(cycles, stages, runs, signals, proposals, scratch);
    }

    /// <summary>The cycles this pass removes: closed, and finished before the cutoff.</summary>
    private const string ExpiringCycles = """
        SELECT id FROM cognitive_cycle
         WHERE status IN (@completed, @failed, @cancelled)
           AND completed_at_utc IS NOT NULL AND completed_at_utc < @before
        """;

    /// <summary>
    /// The working records of the expiring cycles, in an order that never leaves a child without
    /// its parent.
    /// </summary>
    /// <remarks>
    /// These are read only through the cycle — the explanation of why it ran is looked up by the
    /// cycle's id — so once the cycle has gone they are unreachable, and kept they grow by every
    /// call Aurora has ever handled.
    /// </remarks>
    private static readonly string[] ScratchOfExpiringCycles =
    [
        $"DELETE FROM attention_item WHERE set_id IN (SELECT id FROM attention_set WHERE cycle_id IN ({ExpiringCycles}));",
        $"DELETE FROM attention_set WHERE cycle_id IN ({ExpiringCycles});",
        $"DELETE FROM working_item WHERE working_memory_id IN (SELECT id FROM working_memory WHERE cycle_id IN ({ExpiringCycles}));",
        $"DELETE FROM working_memory WHERE cycle_id IN ({ExpiringCycles});",
        $"DELETE FROM thought WHERE cycle_id IN ({ExpiringCycles});",
        $"DELETE FROM deliberation_trace WHERE deliberation_id IN (SELECT id FROM deliberation WHERE cycle_id IN ({ExpiringCycles}));",
        $"DELETE FROM deliberation WHERE cycle_id IN ({ExpiringCycles});",
    ];

    /// <summary>
    /// Runs one statement, or none at all when the policy keeps everything.
    /// </summary>
    /// <remarks>
    /// <see cref="TimeSpan.MaxValue"/> means keep, and it is checked here rather than turned into
    /// a cutoff date: subtracting it from now overflows, and a deployment that asked to keep
    /// everything getting a crash instead would be a poor reward for caution.
    /// </remarks>
    private async Task<int> ExecuteAsync(
        string sql, TimeSpan keepFor, DateTimeOffset now, CancellationToken ct,
        params (string Name, object Value)[] args)
    {
        if (keepFor == TimeSpan.MaxValue)
        {
            return 0;
        }

        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@before", Iso(now - keepFor));
        foreach ((var name, var value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
