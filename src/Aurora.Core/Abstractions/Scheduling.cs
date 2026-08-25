using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// Rhythm without authority (RFC 026).
/// </summary>
/// <remarks>
/// The scheduler decides <i>when</i> something comes due and nothing else. It holds no capability,
/// calls no tool and grants no permission: an occurrence becomes a due run and an event, and what
/// happens next goes through the cognitive cycle like any other request. That is the whole point of
/// keeping it separate — a timer that could act would be a way around every check Aurora has.
/// </remarks>
public interface IScheduler
{
    /// <summary>
    /// Creates a schedule. A routine that reaches outside Aurora needs an approval to exist, and
    /// still needs permission at each occurrence: scheduling is not perpetual consent (rule 3).
    /// </summary>
    Task<Schedule> CreateAsync(ScheduleRequest request, string? approvalRef, CancellationToken ct);

    /// <summary>
    /// Advances every active schedule to <paramref name="now"/>, recording what came due and what
    /// was missed. Creates runs and publishes their events; runs nothing itself.
    /// </summary>
    Task<IReadOnlyList<ScheduleRun>> TickAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>Stops future occurrences without losing the schedule or its history (rule 4).</summary>
    Task<Schedule> PauseAsync(string scheduleId, string actor, CancellationToken ct);

    Task<Schedule> ResumeAsync(string scheduleId, string actor, CancellationToken ct);

    /// <summary>
    /// Ends a schedule for good. Future occurrences stop; past runs and their audit stay (rule 4).
    /// </summary>
    Task<Schedule> DeleteAsync(string scheduleId, string actor, CancellationToken ct);

    /// <summary>
    /// Settles a run left unfinished by a crash, from the cycle it started rather than by assuming.
    /// </summary>
    Task<ScheduleRun> ReconcileAsync(string runId, CancellationToken ct);

    /// <summary>Marks a due run as started, against the cycle that is carrying it out.</summary>
    Task<ScheduleRun> StartAsync(string runId, string cycleId, CancellationToken ct);

    /// <summary>Settles a run the caller saw through to an end.</summary>
    Task<ScheduleRun> FinishAsync(string runId, string status, string? resultRef, CancellationToken ct);

    Task<Schedule?> GetAsync(string scheduleId, CancellationToken ct);

    Task<IReadOnlyList<Schedule>> ListAsync(string? ownerId, CancellationToken ct);

    Task<IReadOnlyList<ScheduleRun>> RunsAsync(string scheduleId, CancellationToken ct);
}
