using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// Governed curiosity (RFC 032): autonomy limited by rule, in its most literal form.
/// </summary>
/// <remarks>
/// This interface is where "limited autonomy" is either real or decorative, so what it cannot do is
/// the specification. It cannot reach a source that is not on an allowlist. It cannot produce
/// anything but a DRAFT goal made of research. It never writes a memory — this type has no way to,
/// deliberately, because rule 4 says researching does not create knowledge and the cleanest
/// enforcement of that is not having the ability.
/// </remarks>
public interface ICuriosityEngine
{
    /// <summary>
    /// Turns gaps Aurora keeps running into questions worth asking, within the policy's limits.
    /// Anything outside them is recorded as REJECTED with its reason, not silently dropped.
    /// </summary>
    Task<IReadOnlyList<CuriosityProposal>> DetectAsync(
        CuriositySnapshot snapshot, CuriosityPolicy policy, CancellationToken ct);

    /// <summary>
    /// Weighs a proposal against the moment: resources, appropriateness and what else is waiting.
    /// </summary>
    /// <remarks>
    /// Returns a decision option rather than a verdict, so the Decision Engine decides — and a
    /// proposal that cannot go ahead comes back carrying its blocking reasons, which is what makes
    /// curiosity the first thing to give way rather than something that argues its case.
    /// </remarks>
    Task<DecisionOption> EvaluateAsync(
        string proposalId, SituationAssessment situation, ResourceBudget budget, CancellationToken ct);

    /// <summary>
    /// Turns an approved proposal into a DRAFT goal of research tasks, and nothing else.
    /// </summary>
    Task<CuriosityProposal> ScheduleAsync(string proposalId, string approvalRef, CancellationToken ct);

    /// <summary>
    /// Attaches what an investigation returned, by reference.
    /// </summary>
    /// <remarks>
    /// A reference, not a belief. Rule 4: what research turns up is an observation that still has
    /// to go through perception and LAW-001 before anything about it is treated as known.
    /// </remarks>
    Task<CuriosityProposal> RecordResultAsync(string proposalId, string observationRef, CancellationToken ct);

    Task<CuriosityProposal?> GetAsync(string proposalId, CancellationToken ct);

    Task<IReadOnlyList<CuriosityProposal>> ListAsync(string? status, CancellationToken ct);

    /// <summary>Expires proposals nobody acted on, so the list is a question queue and not a wishlist.</summary>
    Task<int> ExpireDueAsync(CancellationToken ct);
}
