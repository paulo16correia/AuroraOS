using System.Text.Json;
using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Cryptography;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Observations;

/// <summary>
/// Actions, observations, reflection and learning (LAW-003, RFC 040).
/// </summary>
/// <remarks>
/// LAW-003's justification is the whole design here: without observation, Aurora does not know if
/// she acted, if she failed, or if she should learn. Everything below exists to make it impossible
/// to close an action while any of those three is still unanswered.
/// </remarks>
public sealed class SqliteObservationService : IObservationService
{
    private static readonly IReadOnlyDictionary<string, string[]> AllowedActionStates =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [ActionState.Proposed] = [ActionState.Authorized, ActionState.Cancelled],
            [ActionState.Authorized] = [ActionState.Dispatched, ActionState.Cancelled],
            [ActionState.Dispatched] = [ActionState.Observed, ActionState.Unknown],
            [ActionState.Observed] = [],
            [ActionState.Cancelled] = [],
            [ActionState.Unknown] = [ActionState.Observed],
        };

    private readonly SqliteConnectionFactory _factory;
    private readonly IIncidentService _incidents;
    private readonly IClock _clock;

    public SqliteObservationService(
        SqliteConnectionFactory factory, IIncidentService incidents, IClock clock)
    {
        _factory = factory;
        _incidents = incidents;
        _clock = clock;
    }

    public async Task<AuroraAction> ProposeActionAsync(
        string decisionId, string effectType, string targetRef, string parametersHash,
        bool reversible, CancellationToken ct)
    {
        var action = new AuroraAction(
            Guid.NewGuid().ToString("N"), decisionId, effectType, targetRef, parametersHash,
            reversible, ActionState.Proposed);

        await ExecuteAsync("""
            INSERT INTO aurora_action
                (id, decision_id, effect_type, target_ref, parameters_hash, reversible, state, tool_call_id)
            VALUES (@id, @d, @e, @t, @h, @rev, @s, NULL);
            """, ct,
            ("@id", action.Id), ("@d", decisionId), ("@e", effectType), ("@t", targetRef),
            ("@h", parametersHash), ("@rev", reversible ? 1 : 0), ("@s", action.State))
            .ConfigureAwait(false);

        return action;
    }

    public Task<AuroraAction> AuthorizeActionAsync(string actionId, CancellationToken ct) =>
        MoveAsync(actionId, ActionState.Authorized, null, ct);

    public Task<AuroraAction> DispatchActionAsync(string actionId, string? toolCallId, CancellationToken ct) =>
        MoveAsync(actionId, ActionState.Dispatched, toolCallId, ct);

    public async Task<Observation> RecordAsync(
        string actionId, string observer, string modality, string outcome,
        string? payloadRef, string? externalRef, CancellationToken ct)
    {
        AuroraAction action = await RequireActionAsync(actionId, ct).ConfigureAwait(false);

        if (!ObservationOutcome.IsKnown(outcome))
        {
            throw new ObservationException($"'{outcome}' is not a recognised observation outcome.");
        }

        if (action.State is ActionState.Proposed or ActionState.Authorized)
        {
            // Nothing was dispatched, so there is nothing to have observed. An observation of an
            // action that never left is a fiction.
            throw new ObservationException(
                $"An action in {action.State} has not been dispatched; there is nothing to observe.");
        }

        var observedAt = Iso(_clock.UtcNow);
        var observation = new Observation(
            Guid.NewGuid().ToString("N"), actionId, observer, observedAt, modality, outcome,
            payloadRef, Hashing.Sha256Hex($"{actionId}\n{observer}\n{outcome}\n{observedAt}"),
            externalRef, ObservationState.Raw);

        await ExecuteAsync("""
            INSERT INTO observation
                (id, action_id, observer, observed_at_utc, modality, outcome, payload_ref,
                 integrity, external_ref, state, rejection_reason)
            VALUES (@id, @a, @obs, @at, @mod, @out, @payload, @int, @ext, @state, NULL);
            """, ct,
            ("@id", observation.Id), ("@a", actionId), ("@obs", observer), ("@at", observedAt),
            ("@mod", modality), ("@out", outcome),
            ("@payload", (object?)payloadRef ?? DBNull.Value), ("@int", observation.Integrity),
            ("@ext", (object?)externalRef ?? DBNull.Value), ("@state", observation.State))
            .ConfigureAwait(false);

        // An unknown outcome moves the action to UNKNOWN rather than leaving it dispatched, so it
        // shows up as pending reconciliation instead of looking merely slow.
        if (outcome == ObservationOutcome.Unknown && action.State == ActionState.Dispatched)
        {
            await SetActionStateAsync(actionId, ActionState.Unknown, ct).ConfigureAwait(false);
        }

        return observation;
    }

    public async Task<Observation> ValidateAsync(
        string observationId, bool valid, string? rejectionReason, CancellationToken ct)
    {
        Observation observation = await RequireObservationAsync(observationId, ct).ConfigureAwait(false);

        if (observation.State != ObservationState.Raw)
        {
            throw new ObservationException($"Only a RAW observation is validated; this is {observation.State}.");
        }

        if (!valid && string.IsNullOrWhiteSpace(rejectionReason))
        {
            throw new ObservationException("A rejected observation must record why.");
        }

        Observation settled = observation with
        {
            State = valid ? ObservationState.Validated : ObservationState.Rejected,
            RejectionReason = valid ? null : rejectionReason,
        };

        await ExecuteAsync(
            "UPDATE observation SET state = @s, rejection_reason = @r WHERE id = @id;", ct,
            ("@s", settled.State), ("@r", (object?)settled.RejectionReason ?? DBNull.Value),
            ("@id", observationId)).ConfigureAwait(false);

        return settled;
    }

    public async Task<AuroraAction> ObserveAsync(string actionId, CancellationToken ct)
    {
        AuroraAction action = await RequireActionAsync(actionId, ct).ConfigureAwait(false);
        IReadOnlyList<Observation> observations = await ObservationsAsync(actionId, ct).ConfigureAwait(false);

        var validated = observations.Where(o => o.State == ObservationState.Validated).ToList();

        // LAW-003: an action does not reach OBSERVED without an observation connected. A rejected
        // or still-raw one does not count — that would be closing the loop on something unread.
        if (validated.Count == 0)
        {
            throw new ObservationException(
                "This action has no validated observation; it cannot be closed as OBSERVED.");
        }

        // "We never found out" is not a completed action. It stays UNKNOWN until something says
        // otherwise, which is the whole point of having the state.
        if (validated.All(o => o.Outcome == ObservationOutcome.Unknown))
        {
            throw new ObservationException(
                "Every observation reports an unknown outcome; the action stays UNKNOWN.");
        }

        return await MoveAsync(actionId, ActionState.Observed, action.ToolCallId, ct).ConfigureAwait(false);
    }

    public async Task<Reflection> ReflectAsync(
        string observationId, string outcome, IReadOnlyList<string> lessons,
        IReadOnlyList<LearningProposal> proposals, CancellationToken ct)
    {
        Observation observation = await RequireObservationAsync(observationId, ct).ConfigureAwait(false);

        if (observation.State == ObservationState.Raw)
        {
            throw new ObservationException("An unvalidated observation is not reflected on.");
        }

        var reflectionId = Guid.NewGuid().ToString("N");
        var proposalIds = new List<string>();

        foreach (LearningProposal proposal in proposals)
        {
            var id = Guid.NewGuid().ToString("N");
            proposalIds.Add(id);

            await ExecuteAsync("""
                INSERT INTO learning_proposal
                    (id, reflection_id, type, change_set_json, evaluation_plan, rollback_plan, state,
                     expected_benefit, risk, evidence_refs)
                VALUES (@id, @r, @t, @cs, @eval, @roll, @s, @benefit, @risk, @evidence);
                """, ct,
                ("@id", id), ("@r", reflectionId), ("@t", proposal.Type),
                ("@cs", proposal.ChangeSetJson), ("@eval", proposal.EvaluationPlan),
                ("@roll", proposal.RollbackPlan), ("@s", LearningProposalState.Proposed),
                ("@benefit", proposal.ExpectedBenefit),
                ("@risk", proposal.Risk),

                // The proposal inherits the reflection's evidence when it names none of its own:
                // rule 1 asks for concrete evidence, and a proposal born of an observation has it
                // whether or not the proposer thought to repeat it.
                ("@evidence", string.Join(',', proposal.EvidenceRefs.Count > 0
                    ? proposal.EvidenceRefs
                    : new[] { observation.Id })))
                .ConfigureAwait(false);
        }

        var reflection = new Reflection(
            reflectionId, observationId, outcome, [observation.Id], lessons, proposalIds,
            ReflectionState.Draft);

        await ExecuteAsync("""
            INSERT INTO reflection
                (id, observation_id, outcome, evidence_refs, lessons, proposal_refs, state)
            VALUES (@id, @o, @out, @ev, @lessons, @props, @s);
            """, ct,
            ("@id", reflectionId), ("@o", observationId), ("@out", outcome),
            ("@ev", string.Join(',', reflection.EvidenceRefs)),
            ("@lessons", string.Join('\n', lessons)),
            ("@props", string.Join(',', proposalIds)), ("@s", reflection.State)).ConfigureAwait(false);

        return reflection;
    }

    public async Task<Reflection> DecideReflectionAsync(string reflectionId, bool accept, CancellationToken ct)
    {
        Reflection reflection = await RequireReflectionAsync(reflectionId, ct).ConfigureAwait(false);

        if (reflection.State != ReflectionState.Draft)
        {
            throw new ObservationException($"Only a DRAFT reflection is decided; this is {reflection.State}.");
        }

        var state = accept ? ReflectionState.Accepted : ReflectionState.Rejected;
        await ExecuteAsync("UPDATE reflection SET state = @s WHERE id = @id;", ct,
            ("@s", state), ("@id", reflectionId)).ConfigureAwait(false);

        return reflection with { State = state };
    }

    public async Task<LearningProposal> DecideLearningAsync(
        string proposalId, bool approve, CancellationToken ct)
    {
        LearningProposal proposal = await RequireProposalAsync(proposalId, ct).ConfigureAwait(false);

        if (proposal.State != LearningProposalState.Proposed)
        {
            throw new ObservationException($"Only a PROPOSED change is decided; this is {proposal.State}.");
        }

        var state = approve ? LearningProposalState.Approved : LearningProposalState.Rejected;
        await ExecuteAsync("UPDATE learning_proposal SET state = @s WHERE id = @id;", ct,
            ("@s", state), ("@id", proposalId)).ConfigureAwait(false);

        return proposal with { State = state };
    }

    public async Task<LearningProposal> ApplyLearningAsync(
        string proposalId, CancellationToken ct, bool acceptInconclusive = false)
    {
        LearningProposal proposal = await RequireProposalAsync(proposalId, ct).ConfigureAwait(false);

        // RFC 021's Learning stage applies approved changes and nothing else. A proposal that
        // nobody decided on is a suggestion, and a system that deploys its own suggestions is not
        // learning — it is drifting.
        if (proposal.State is not (LearningProposalState.Approved or LearningProposalState.Testing))
        {
            throw new ObservationException(
                $"Only an APPROVED or tested change is applied; this is {proposal.State}.");
        }

        // Rule 2: a low-risk memory change is the only thing that goes straight from approval to
        // application. It is bounded by RFC 03 — a memory is provenanced, revisable and
        // forgettable — so the cost of getting one wrong is a correction, not a behaviour change.
        var automatic = proposal is
        {
            Type: LearningProposalType.Memory,
            Risk: LearningRisk.Low,
            State: LearningProposalState.Approved,
        };

        if (!automatic)
        {
            // Rule 3: personality, policy, tools, templates and automation must be approved,
            // tested and reversible. Approved is above; reversible is the rollback plan; tested is
            // this, and without it the TESTING state was a state nothing ever entered.
            IReadOnlyList<EvaluationRun> runs =
                await EvaluationsAsync(proposalId, ct).ConfigureAwait(false);

            EvaluationRun latest = runs.Count > 0
                ? runs[^1]
                : throw new ObservationException(
                    $"A {proposal.Type} change is applied only after it has been evaluated; "
                    + "this one never has been.");

            if (latest.Verdict == EvaluationVerdict.Fail)
            {
                throw new ObservationException(
                    "The last evaluation failed; this change is not applied.");
            }

            if (latest.Verdict == EvaluationVerdict.Inconclusive && !acceptInconclusive)
            {
                throw new ObservationException(
                    "The last evaluation was inconclusive; RFC 08 requires a human decision "
                    + "before this is applied.");
            }

            if (string.IsNullOrWhiteSpace(proposal.RollbackPlan))
            {
                // Reversible is not an afterthought in rule 3; it is one of the three conditions.
                throw new ObservationException(
                    "This change declares no rollback plan, so applying it could not be undone.");
            }
        }

        await ExecuteAsync("UPDATE learning_proposal SET state = @s WHERE id = @id;", ct,
            ("@s", LearningProposalState.Deployed), ("@id", proposalId)).ConfigureAwait(false);

        return proposal with { State = LearningProposalState.Deployed };
    }

    /// <summary>
    /// The three dimensions RFC 08 rule 4 makes mandatory. Textual quality is deliberately not
    /// among them: the rule says "not just" textual quality, and it is the one Aurora has no way to
    /// judge locally, so claiming a number for it would be the dishonest half of this method.
    /// </summary>
    private const string SecurityDimension = "security_regression";
    private const string CostDimension = "cost";
    private const string PrivacyDimension = "privacy";

    /// <summary>
    /// Whether the change is well-formed enough to be the thing it says it is.
    /// </summary>
    /// <remarks>
    /// Not whether it will work — Aurora cannot know that about an arbitrary change, and a
    /// dimension claiming to would be the dishonest half of this method. What it can check is that
    /// the change set parses, is an object, and is not empty: three ways a proposal is wrong that
    /// nothing else here would catch, and that would otherwise be found by whatever consumed it
    /// after it was applied.
    /// </remarks>
    private const string CorrectnessDimension = "correctness";

    /// <summary>
    /// Whether there is a way back.
    /// </summary>
    /// <remarks>
    /// Also enforced at application, and measured here as well on purpose: a proposal whose
    /// rollback plan is missing should be told so while somebody is still deciding about it,
    /// rather than at the moment they try to apply it.
    /// </remarks>
    private const string ReversibilityDimension = "reversibility";

    /// <summary>
    /// Words in a change set that widen what Aurora may do rather than what it knows.
    /// </summary>
    /// <remarks>
    /// Crude on purpose, and it errs towards finding a regression that is not there. A false
    /// positive costs a human decision; a false negative deploys a policy change nobody tested.
    /// </remarks>
    private static readonly string[] AuthorityWords =
        ["policy", "capability", "permission", "grant", "allow", "connector", "credential", "vault"];

    public async Task<EvaluationRun> EvaluateAsync(
        string proposalId, string testScope, CancellationToken ct)
    {
        LearningProposal proposal = await RequireProposalAsync(proposalId, ct).ConfigureAwait(false);

        // Approved, then tested, then applied — rule 3's order. Testing something nobody has agreed
        // to look at is work; testing something already deployed is not a test.
        if (proposal.State is not (LearningProposalState.Approved or LearningProposalState.Testing))
        {
            throw new ObservationException(
                $"Only an APPROVED or TESTING change is evaluated; this is {proposal.State}.");
        }

        var metrics = new List<Metric>
        {
            Correctness(proposal),
            Security(proposal),
            Cost(proposal),
            Privacy(proposal),
            Reversibility(proposal),
        };

        var verdict = metrics.Any(m => m.Regressed)
            ? EvaluationVerdict.Fail
            : metrics.All(m => m.Measured)
                ? EvaluationVerdict.Pass
                : EvaluationVerdict.Inconclusive;

        var run = new EvaluationRun(
            Guid.NewGuid().ToString("N"), proposalId, testScope,

            // What it ran against: the evidence the proposal carries. Nothing else was consulted,
            // and saying so is what makes the verdict readable a year from now.
            string.Join(',', proposal.EvidenceRefs),
            AuroraJson.Serialize(metrics),
            verdict,
            Iso(_clock.UtcNow));

        await ExecuteAsync("""
            INSERT INTO evaluation_run
                (id, proposal_id, test_scope, dataset_ref, metrics_json, verdict, executed_at_utc)
            VALUES (@id, @p, @scope, @data, @metrics, @v, @at);
            """, ct,
            ("@id", run.Id), ("@p", run.ProposalId), ("@scope", run.TestScope),
            ("@data", run.DatasetRef), ("@metrics", run.MetricsJson),
            ("@v", run.Verdict), ("@at", run.ExecutedAtUtc)).ConfigureAwait(false);

        // A failure ends the proposal. An inconclusive one stays in test, which is RFC 08's limit
        // case verbatim: keep it there and require a human decision.
        var next = verdict == EvaluationVerdict.Fail
            ? LearningProposalState.Rejected
            : LearningProposalState.Testing;

        await ExecuteAsync("UPDATE learning_proposal SET state = @s WHERE id = @id;", ct,
            ("@s", next), ("@id", proposalId)).ConfigureAwait(false);

        return run;
    }

    public async Task<IReadOnlyList<EvaluationRun>> EvaluationsAsync(
        string proposalId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, proposal_id, test_scope, dataset_ref, metrics_json, verdict, executed_at_utc
              FROM evaluation_run WHERE proposal_id = @p ORDER BY executed_at_utc ASC, rowid ASC;
            """;
        command.Parameters.AddWithValue("@p", proposalId);

        var runs = new List<EvaluationRun>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            runs.Add(new EvaluationRun(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6)));
        }

        return runs;
    }

    /// <summary>Is this well-formed enough to be the thing it says it is?</summary>
    private static Metric Correctness(LearningProposal proposal)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(proposal.ChangeSetJson);

            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return new Metric(CorrectnessDimension, Measured: true, Regressed: true,
                    "the change set is not an object");
            }

            return document.RootElement.EnumerateObject().Any()
                ? new Metric(CorrectnessDimension, Measured: true, Regressed: false,
                    "the change set is a well-formed object")
                : new Metric(CorrectnessDimension, Measured: true, Regressed: true,
                    "the change set is empty; there is nothing here to apply");
        }
        catch (JsonException)
        {
            return new Metric(CorrectnessDimension, Measured: true, Regressed: true,
                "the change set is not valid JSON");
        }
    }

    /// <summary>Is there a way back from this?</summary>
    private static Metric Reversibility(LearningProposal proposal) =>
        string.IsNullOrWhiteSpace(proposal.RollbackPlan)
            ? new Metric(ReversibilityDimension, Measured: true, Regressed: true,
                "no rollback plan; applying this could not be undone")
            : new Metric(ReversibilityDimension, Measured: true, Regressed: false,
                $"undone by: {proposal.RollbackPlan}");

    /// <summary>One dimension's result, and whether Aurora was able to look at all.</summary>
    private sealed record Metric(string Dimension, bool Measured, bool Regressed, string Detail);

    /// <summary>
    /// Does this change widen Aurora's authority beyond what its own type claims?
    /// </summary>
    /// <remarks>
    /// A memory change that mentions policy or a capability is not a memory change. This is the
    /// substitution that matters: a proposal declared harmless carrying something that is not.
    /// </remarks>
    private static Metric Security(LearningProposal proposal)
    {
        if (proposal.Type is LearningProposalType.PolicySuggestion)
        {
            // A policy suggestion is allowed to be about policy. What it is not allowed to do is
            // apply itself, and rule 3 already handles that by requiring approval and a test.
            return new Metric(SecurityDimension, Measured: true, Regressed: false,
                "a policy suggestion, reviewed as one");
        }

        var found = AuthorityWords
            .Where(word => proposal.ChangeSetJson.Contains(word, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return found.Count > 0
            ? new Metric(SecurityDimension, Measured: true, Regressed: true,
                $"a {proposal.Type} change set mentions {string.Join(", ", found)}")
            : new Metric(SecurityDimension, Measured: true, Regressed: false,
                "widens nothing Aurora may do");
    }

    /// <summary>
    /// What this change costs to run, if the proposal said.
    /// </summary>
    /// <remarks>
    /// Aurora cannot infer the running cost of an arbitrary change from its JSON, and will not
    /// pretend to. A proposal that declares no evaluation plan has told the evaluator nothing to
    /// measure, and the honest answer is that this was not measured — which makes the verdict
    /// inconclusive and sends it to a human, rather than passing by omission.
    /// </remarks>
    private static Metric Cost(LearningProposal proposal) =>
        string.IsNullOrWhiteSpace(proposal.EvaluationPlan)
            ? new Metric(CostDimension, Measured: false, Regressed: false,
                "the proposal declares no evaluation plan, so there is nothing to measure against")
            : new Metric(CostDimension, Measured: true, Regressed: false,
                $"bounded by the declared plan: {proposal.EvaluationPlan}");

    /// <summary>
    /// Does this change carry something that should never have been in it?
    /// </summary>
    private static Metric Privacy(LearningProposal proposal) =>
        SecretShape.Matches(proposal.ChangeSetJson)
            ? new Metric(PrivacyDimension, Measured: true, Regressed: true,
                "the change set carries something shaped like a credential")
            : new Metric(PrivacyDimension, Measured: true, Regressed: false,
                "no credential shape in the change set");

    public async Task<LearningProposal> RollBackLearningAsync(
        string proposalId, string failure, CancellationToken ct)
    {
        LearningProposal proposal = await RequireProposalAsync(proposalId, ct).ConfigureAwait(false);

        if (proposal.State != LearningProposalState.Deployed)
        {
            // Rolling back something that was never applied would record an undoing that did not
            // happen, and would open an incident about a change that never took effect.
            throw new ObservationException(
                $"Only a DEPLOYED change is rolled back; this is {proposal.State}.");
        }

        await ExecuteAsync("UPDATE learning_proposal SET state = @s WHERE id = @id;", ct,
            ("@s", LearningProposalState.RolledBack), ("@id", proposalId)).ConfigureAwait(false);

        // Block new application: ROLLED_BACK is not a state ApplyLearningAsync accepts, so getting
        // this change back in takes a new proposal, a new decision and a new evaluation.

        // And open an incident, which is the part the limit case names and the part a system that
        // merely undid itself would skip. HIGH rather than CRITICAL: something Aurora changed about
        // its own behaviour did not work, which is serious and is not an attack.
        await _incidents.OpenAsync(
            new SecurityEvent(
                string.Empty, SecuritySeverity.High, SecurityEventType.UndeclaredBehaviour,
                Guid.NewGuid().ToString("N"), "learning", $"learning/{proposalId}", null,

                // The rollback plan is the evidence: what was supposed to undo it, so whoever
                // arrives can tell whether the undoing was possible at all.
                $"rollback:{proposal.RollbackPlan}", string.Empty),
            ct).ConfigureAwait(false);

        return proposal with { State = LearningProposalState.RolledBack };
    }

    public async Task<AuroraAction?> GetActionAsync(string actionId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = ActionSelect + " WHERE id = @id;";
        command.Parameters.AddWithValue("@id", actionId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadAction(reader) : null;
    }

    public async Task<IReadOnlyList<Observation>> ObservationsAsync(string actionId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, action_id, observer, observed_at_utc, modality, outcome, payload_ref,
                   integrity, external_ref, state, rejection_reason
              FROM observation WHERE action_id = @a ORDER BY observed_at_utc ASC, rowid ASC;
            """;
        command.Parameters.AddWithValue("@a", actionId);

        var rows = new List<Observation>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new Observation(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        return rows;
    }

    public async Task<IReadOnlyList<AuroraAction>> UnobservedAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = ActionSelect + " WHERE state IN (@dispatched, @unknown);";
        command.Parameters.AddWithValue("@dispatched", ActionState.Dispatched);
        command.Parameters.AddWithValue("@unknown", ActionState.Unknown);

        var rows = new List<AuroraAction>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(ReadAction(reader));
        }

        return rows;
    }

    // ---- internals ----

    private async Task<AuroraAction> MoveAsync(
        string actionId, string target, string? toolCallId, CancellationToken ct)
    {
        AuroraAction action = await RequireActionAsync(actionId, ct).ConfigureAwait(false);

        if (!AllowedActionStates.TryGetValue(action.State, out var allowed)
            || !allowed.Contains(target, StringComparer.Ordinal))
        {
            throw new ObservationException($"{action.State} does not transition to {target}.");
        }

        await ExecuteAsync(
            "UPDATE aurora_action SET state = @s, tool_call_id = @t WHERE id = @id;", ct,
            ("@s", target), ("@t", (object?)(toolCallId ?? action.ToolCallId) ?? DBNull.Value),
            ("@id", actionId)).ConfigureAwait(false);

        return action with { State = target, ToolCallId = toolCallId ?? action.ToolCallId };
    }

    private Task SetActionStateAsync(string actionId, string state, CancellationToken ct) =>
        ExecuteAsync("UPDATE aurora_action SET state = @s WHERE id = @id;", ct,
            ("@s", state), ("@id", actionId));

    private async Task<AuroraAction> RequireActionAsync(string actionId, CancellationToken ct) =>
        await GetActionAsync(actionId, ct).ConfigureAwait(false)
        ?? throw new ObservationException("Unknown action.");

    private async Task<Observation> RequireObservationAsync(string id, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, action_id, observer, observed_at_utc, modality, outcome, payload_ref,
                   integrity, external_ref, state, rejection_reason
              FROM observation WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@id", id);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new ObservationException("Unknown observation.");
        }

        return new Observation(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10));
    }

    private async Task<Reflection> RequireReflectionAsync(string id, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, observation_id, outcome, evidence_refs, lessons, proposal_refs, state
              FROM reflection WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@id", id);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new ObservationException("Unknown reflection.");
        }

        return new Reflection(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            Split(reader.GetString(3)),
            reader.GetString(4).Split('\n', StringSplitOptions.RemoveEmptyEntries),
            Split(reader.GetString(5)), reader.GetString(6));
    }

    private async Task<LearningProposal> RequireProposalAsync(string id, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, reflection_id, type, change_set_json, evaluation_plan, rollback_plan, state,
                   expected_benefit, risk, evidence_refs
              FROM learning_proposal WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@id", id);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new ObservationException("Unknown learning proposal.");
        }

        return new LearningProposal(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6),
            reader.GetString(7), reader.GetString(8), Split(reader.GetString(9)));
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((var name, var value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private const string ActionSelect = """
        SELECT id, decision_id, effect_type, target_ref, parameters_hash, reversible, state, tool_call_id
          FROM aurora_action
        """;

    private static AuroraAction ReadAction(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.GetInt32(5) == 1, r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7));

    private static IReadOnlyList<string> Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries);

    private static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
