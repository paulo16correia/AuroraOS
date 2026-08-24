using System.Globalization;
using Aurora.Adapters.Persistence;
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
    private readonly IClock _clock;

    public SqliteObservationService(SqliteConnectionFactory factory, IClock clock)
    {
        _factory = factory;
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
                    (id, reflection_id, type, change_set_json, evaluation_plan, rollback_plan, state)
                VALUES (@id, @r, @t, @cs, @eval, @roll, @s);
                """, ct,
                ("@id", id), ("@r", reflectionId), ("@t", proposal.Type),
                ("@cs", proposal.ChangeSetJson), ("@eval", proposal.EvaluationPlan),
                ("@roll", proposal.RollbackPlan), ("@s", LearningProposalState.Proposed))
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

    public async Task<LearningProposal> ApplyLearningAsync(string proposalId, CancellationToken ct)
    {
        LearningProposal proposal = await RequireProposalAsync(proposalId, ct).ConfigureAwait(false);

        // RFC 021's Learning stage applies approved changes and nothing else. A proposal that
        // nobody decided on is a suggestion, and a system that deploys its own suggestions is not
        // learning — it is drifting.
        if (proposal.State != LearningProposalState.Approved)
        {
            throw new ObservationException(
                $"Only an APPROVED change is applied; this is {proposal.State}.");
        }

        await ExecuteAsync("UPDATE learning_proposal SET state = @s WHERE id = @id;", ct,
            ("@s", LearningProposalState.Deployed), ("@id", proposalId)).ConfigureAwait(false);

        return proposal with { State = LearningProposalState.Deployed };
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
            SELECT id, reflection_id, type, change_set_json, evaluation_plan, rollback_plan, state
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
            reader.GetString(4), reader.GetString(5), reader.GetString(6));
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
