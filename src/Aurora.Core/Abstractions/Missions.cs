using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// Purpose above goals (RFC 052).
/// </summary>
/// <remarks>
/// Nothing on this interface grants anything, and that is rule 1: a mission is not an execution
/// order and does not replace the approval a risky goal or task needs. Aligning a goal to a mission
/// changes what it is <i>for</i>, never what it is allowed to do.
/// <para>
/// Every operation takes an actor, because rule 4 says missions are reviewed, paused and removed by
/// their owner and do not evolve by automatic inference. Aurora does not get to decide what it is
/// for.
/// </para>
/// </remarks>
public interface IMissionService
{
    /// <summary>
    /// Records a mission the owner defined. Needs an approval, because deciding what Aurora is for
    /// over months is a decision only a person makes (rule 4).
    /// </summary>
    Task<Mission> CreateAsync(MissionDefinition definition, string approvalRef, CancellationToken ct);

    /// <summary>Puts a goal under a mission's purpose.</summary>
    Task<Goal> AlignAsync(string goalId, string missionId, string actor, CancellationToken ct);

    /// <summary>
    /// Reports how a mission stands: what is aligned to it, what is drifting unaligned, and whether
    /// the mission itself is overdue a look. Reports, and changes nothing.
    /// </summary>
    Task<MissionReview> ReviewAsync(string missionId, CancellationToken ct);

    Task<Mission> PauseAsync(string missionId, string actor, CancellationToken ct);

    Task<Mission> ActivateAsync(string missionId, string actor, CancellationToken ct);

    /// <summary>Ends a mission. Goals that were aligned to it keep their history.</summary>
    Task<Mission> RetireAsync(string missionId, string actor, CancellationToken ct);

    Task<Mission?> GetAsync(string missionId, CancellationToken ct);

    Task<IReadOnlyList<Mission>> ListAsync(string? owner, CancellationToken ct);
}
