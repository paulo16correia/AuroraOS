using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>Closes the loop between intention and the world (LAW-003, RFC 040).</summary>
public interface IObservationService
{
    Task<AuroraAction> ProposeActionAsync(
        string decisionId, string effectType, string targetRef, string parametersHash,
        bool reversible, CancellationToken ct);

    Task<AuroraAction> AuthorizeActionAsync(string actionId, CancellationToken ct);

    Task<AuroraAction> DispatchActionAsync(string actionId, string? toolCallId, CancellationToken ct);

    /// <summary>Records what came back, as RAW. Nothing is believed before validation.</summary>
    Task<Observation> RecordAsync(
        string actionId, string observer, string modality, string outcome,
        string? payloadRef, string? externalRef, CancellationToken ct);

    /// <summary>Moves an observation to VALIDATED or REJECTED, with a reason when rejected.</summary>
    Task<Observation> ValidateAsync(
        string observationId, bool valid, string? rejectionReason, CancellationToken ct);

    /// <summary>
    /// Closes the action. Refuses without at least one validated observation attached (LAW-003),
    /// and refuses to call an unknown outcome a success.
    /// </summary>
    Task<AuroraAction> ObserveAsync(string actionId, CancellationToken ct);

    /// <summary>Reflects on an observation. A reflection with no lessons is still a reflection.</summary>
    Task<Reflection> ReflectAsync(
        string observationId, string outcome, IReadOnlyList<string> lessons,
        IReadOnlyList<LearningProposal> proposals, CancellationToken ct);

    /// <summary>Accepts or rejects a reflection.</summary>
    Task<Reflection> DecideReflectionAsync(string reflectionId, bool accept, CancellationToken ct);

    /// <summary>
    /// Tests a proposal before anything is changed by it (RFC 08 <c>Evaluator.run</c>).
    /// </summary>
    /// <param name="testScope">
    /// What the caller asked to be exercised. Recorded verbatim on the run, so a verdict can be
    /// read against the question it answered rather than against a later assumption about it.
    /// </param>
    /// <remarks>
    /// Rule 4 is the whole point: an assessment measures security regression, cost and privacy, not
    /// only whether the change reads well. Each of those is reported with whether it could be
    /// measured, and a dimension Aurora could not measure produces
    /// <see cref="EvaluationVerdict.Inconclusive"/> rather than a pass by omission.
    /// </remarks>
    Task<EvaluationRun> EvaluateAsync(string proposalId, string testScope, CancellationToken ct);

    /// <summary>Every evaluation of one proposal, oldest first.</summary>
    Task<IReadOnlyList<EvaluationRun>> EvaluationsAsync(string proposalId, CancellationToken ct);

    /// <summary>
    /// Applies a learning proposal. Only an approved one is applied — RFC 021's Learning stage
    /// applies approved changes and nothing else.
    /// </summary>
    /// <param name="acceptInconclusive">
    /// The human decision RFC 08's limit case requires when a metric moved both ways or could not
    /// be measured. Never a default: an inconclusive evaluation that applied itself would be a
    /// system deciding it had passed its own test.
    /// </param>
    /// <remarks>
    /// Rule 2 and rule 3 meet here. A low-risk <see cref="LearningProposalType.Memory"/> change may
    /// go straight from approval to application; everything else — personality, policy, tools,
    /// templates, automation — must have been tested first, and is refused until it has.
    /// </remarks>
    Task<LearningProposal> ApplyLearningAsync(
        string proposalId, CancellationToken ct, bool acceptInconclusive = false);

    Task<LearningProposal> DecideLearningAsync(string proposalId, bool approve, CancellationToken ct);

    Task<AuroraAction?> GetActionAsync(string actionId, CancellationToken ct);

    Task<IReadOnlyList<Observation>> ObservationsAsync(string actionId, CancellationToken ct);

    /// <summary>Actions dispatched and never observed, for the reconciliation surface (LAW-003).</summary>
    Task<IReadOnlyList<AuroraAction>> UnobservedAsync(CancellationToken ct);
}
