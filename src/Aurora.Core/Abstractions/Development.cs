using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// How an instance gains operational confidence without gaining power (RFC 037).
/// </summary>
/// <remarks>
/// The distinction the whole RFC turns on: development changes how much of <i>Aurora's own</i>
/// caution sits on top of the rules, and never the rules. A stage can decide that Aurora stops
/// double-checking things it has done reliably a hundred times; it cannot decide that Aurora may do
/// something policy refuses. Confidence grows through evidence and can shrink through incident.
/// </remarks>
public interface IDevelopmentModel
{
    /// <summary>
    /// Reads the evidence and reports whether it supports moving on, and what is missing.
    /// </summary>
    /// <remarks>
    /// Rule 1: promotion needs evidence of reliability, not elapsed time. Nothing here counts days.
    /// </remarks>
    Task<DevelopmentAssessment> AssessAsync(string mindId, CancellationToken ct);

    /// <summary>Proposes a move. Refused when the evidence does not support it.</summary>
    Task<DevelopmentProposal> ProposeTransitionAsync(
        string mindId, string targetStageId, CancellationToken ct);

    /// <summary>
    /// Applies a proposed move. Needs the owner's approval (rule 4: visible and reversible).
    /// </summary>
    Task<DevelopmentState> ApplyTransitionAsync(
        string proposalId, string approvalRef, string actor, CancellationToken ct);

    /// <summary>
    /// Pulls the phase back after an incident, in the scope the incident touched (rule 2).
    /// </summary>
    Task<DevelopmentState> RestrictAsync(
        string mindId, string scope, string incidentRef, string reason, CancellationToken ct);

    /// <summary>
    /// Whether development wants this confirmed, on top of whatever policy already requires.
    /// </summary>
    /// <remarks>
    /// Only ever adds. A <c>false</c> here means development has nothing further to ask — it never
    /// means the action may proceed, which remains the Kernel's answer to give.
    /// </remarks>
    Task<bool> WantsConfirmationAsync(
        string mindId, CapabilityDescriptor capability, CancellationToken ct);

    Task<DevelopmentState> CurrentAsync(string mindId, CancellationToken ct);

    Task<IReadOnlyList<DevelopmentProposal>> ProposalsAsync(string mindId, CancellationToken ct);
}
