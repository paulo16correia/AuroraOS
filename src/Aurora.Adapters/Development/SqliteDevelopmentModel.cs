using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Cryptography;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Development;

/// <summary>
/// Operational maturity, earned rather than accrued (RFC 037).
/// </summary>
/// <remarks>
/// The distinction this class exists to hold: development changes how much of <b>Aurora's own</b>
/// caution sits on top of the rules, and never the rules themselves. Growing up means Aurora stops
/// double-checking things it has done reliably; it never means Aurora may do something policy
/// refuses. Every stage's ceiling is a floor on caution, not a grant of authority.
/// </remarks>
public sealed class SqliteDevelopmentModel : IDevelopmentModel
{
    /// <summary>
    /// The stages an instance moves through.
    /// </summary>
    /// <remarks>
    /// Three, and the criteria are counted per risk level rather than in total. Many low-risk
    /// successes say nothing about medium-risk reliability, which is RFC 037's own limit case and
    /// the reason evidence is never summed across levels.
    /// </remarks>
    public static DevelopmentProfile DefaultProfile { get; } = new(
        "development/default", "genome/local",
        [
            new DevelopmentStage(
                "stage/supervised", "Supervised", RiskLevel.Low,
                ConfirmationRules: ["confirm everything, including what policy would allow"],
                CapabilityConstraints: ["nothing that reaches outside Aurora"],
                PromotionCriteria: [new PromotionCriterion(RiskLevel.Low, 20, 0)],
                RegressionCriteria: []),

            new DevelopmentStage(
                "stage/assisting", "Assisting", RiskLevel.Low,
                ConfirmationRules: ["low-risk reads run as policy allows", "everything else is confirmed"],
                CapabilityConstraints: ["effectful capabilities are confirmed regardless of policy"],

                // Medium-risk evidence, counted on its own. Twenty successful clock readings are
                // not an argument about writing files.
                PromotionCriteria: [new PromotionCriterion(RiskLevel.Medium, 15, 1)],
                RegressionCriteria: ["any failure at MEDIUM or above"]),

            new DevelopmentStage(
                "stage/trusted", "Trusted", RiskLevel.Medium,
                ConfirmationRules: ["policy decides; development adds nothing further"],
                CapabilityConstraints: ["high-risk capabilities are always confirmed"],
                PromotionCriteria: [],
                RegressionCriteria: ["any failure at MEDIUM or above", "any revoked capability"]),
        ]);

    private readonly SqliteConnectionFactory _factory;
    private readonly IAuditStore _audit;
    private readonly ICapabilityRegistry _registry;
    private readonly DevelopmentProfile _profile;
    private readonly IEventBus _bus;
    private readonly IClock _clock;

    public SqliteDevelopmentModel(
        SqliteConnectionFactory factory,
        IAuditStore audit,
        ICapabilityRegistry registry,
        DevelopmentProfile profile,
        IClock clock, IEventBus bus)
    {
        _factory = factory;
        _audit = audit;
        _registry = registry;
        _profile = profile;
        _clock = clock;
        _bus = bus;
    }

    public async Task<DevelopmentAssessment> AssessAsync(string mindId, CancellationToken ct)
    {
        DevelopmentState state = await CurrentAsync(mindId, ct).ConfigureAwait(false);
        DevelopmentStage stage = StageOf(state.CurrentStageId);
        DevelopmentStage? next = NextAfter(stage.Id);

        IReadOnlyList<ReliabilityEvidence> evidence =
            await GatherAsync(ct).ConfigureAwait(false);

        var missing = new List<string>();

        if (next is null)
        {
            missing.Add("this is the last stage; there is nothing further to be promoted to");
            return Assessment(mindId, state, null, evidence, false, missing);
        }

        // Rule 2: a restricted instance is not a candidate for promotion. Whatever the counts say,
        // something went wrong recently enough that it is still being held back.
        if (state.Status is DevelopmentStatus.Restricted or DevelopmentStatus.Paused)
        {
            missing.Add($"the instance is {state.Status}: {state.Reason ?? "no reason recorded"}");
            return Assessment(mindId, state, next.Id, evidence, false, missing);
        }

        foreach (PromotionCriterion criterion in stage.PromotionCriteria)
        {
            ReliabilityEvidence at = evidence.FirstOrDefault(e => e.Risk == criterion.Risk)
                ?? new ReliabilityEvidence(criterion.Risk, 0, 0, []);

            // Rule 1: counted, not waited for. There is no clause here about elapsed time, because
            // a month of doing nothing is not evidence of anything.
            if (at.Successes < criterion.MinimumSuccesses)
            {
                missing.Add(
                    $"{criterion.MinimumSuccesses - at.Successes} more successful {criterion.Risk} "
                    + $"action(s) (have {at.Successes})");
            }

            if (at.Failures > criterion.MaximumFailures)
            {
                missing.Add(
                    $"{at.Failures} failure(s) at {criterion.Risk}, and this stage allows "
                    + $"{criterion.MaximumFailures}");
            }
        }

        // Limit case: insufficient data holds the current phase. Nothing is promoted by default,
        // and a stage with no criteria at all is not a stage anybody graduates from quietly.
        if (stage.PromotionCriteria.Count == 0)
        {
            missing.Add("this stage states no promotion criteria, so nothing satisfies them");
        }

        return Assessment(mindId, state, next.Id, evidence, missing.Count == 0, missing);
    }

    public async Task<DevelopmentProposal> ProposeTransitionAsync(
        string mindId, string targetStageId, CancellationToken ct)
    {
        DevelopmentState state = await CurrentAsync(mindId, ct).ConfigureAwait(false);
        DevelopmentStage target = StageOf(targetStageId);
        DevelopmentAssessment assessment = await AssessAsync(mindId, ct).ConfigureAwait(false);

        var forward = IndexOf(target.Id) > IndexOf(state.CurrentStageId);

        // Moving forward needs the evidence. Moving back never does — pulling autonomy in is
        // something that should always be available, and asking for proof first would make caution
        // harder than confidence.
        if (forward && !assessment.ReadyToPromote)
        {
            throw new DevelopmentException(
                $"The evidence does not support this yet: {string.Join("; ", assessment.Missing)}");
        }

        if (forward && target.Id != assessment.NextStageId)
        {
            throw new DevelopmentException(
                $"Stages are moved through one at a time; the next one is {assessment.NextStageId}.");
        }

        var proposal = new DevelopmentProposal(
            Guid.NewGuid().ToString("N"), mindId, state.CurrentStageId, target.Id,
            assessment.Evidence.SelectMany(e => e.Refs).Distinct(StringComparer.Ordinal).ToList(),
            forward
                ? $"evidence supports {target.Name}"
                : $"pulling back to {target.Name}",
            Iso(_clock.UtcNow), ProposalStatus.Proposed);

        await ExecuteAsync("""
            INSERT INTO development_proposal
                (id, mind_id, from_stage_id, to_stage_id, evidence_refs, rationale,
                 proposed_at_utc, status, approval_ref)
            VALUES (@id, @mind, @from, @to, @evidence, @rationale, @at, @status, NULL);
            """, ct,
            ("@id", proposal.Id), ("@mind", mindId), ("@from", proposal.FromStageId),
            ("@to", proposal.ToStageId),
            ("@evidence", string.Join('\n', proposal.EvidenceRefs)),
            ("@rationale", proposal.Rationale), ("@at", proposal.ProposedAtUtc),
            ("@status", proposal.Status)).ConfigureAwait(false);

        return proposal;
    }

    public async Task<DevelopmentState> ApplyTransitionAsync(
        string proposalId, string approvalRef, string actor, CancellationToken ct)
    {
        DevelopmentProposal proposal = await ProposalAsync(proposalId, ct).ConfigureAwait(false);

        if (proposal.Status != ProposalStatus.Proposed)
        {
            throw new DevelopmentException($"This proposal is {proposal.Status}.");
        }

        var forward = IndexOf(proposal.ToStageId) > IndexOf(proposal.FromStageId);

        // Rule 4: a change in autonomy is the owner's, and it is reversible. Approval is required
        // to gain autonomy and not to give it up — needing permission to be more careful would be
        // the wrong way round.
        if (forward && string.IsNullOrWhiteSpace(approvalRef))
        {
            throw new DevelopmentException("Gaining autonomy needs the owner's approval.");
        }

        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new DevelopmentException("A change of stage records who made it.");
        }

        DevelopmentState state = await CurrentAsync(proposal.MindId, ct).ConfigureAwait(false);

        DevelopmentState moved = state with
        {
            CurrentStageId = proposal.ToStageId,
            EvidenceRefs = proposal.EvidenceRefs,
            AssessmentAtUtc = Iso(_clock.UtcNow),

            // Moving back clears the restriction that caused it; moving forward starts clean.
            Status = DevelopmentStatus.Active,
            RestrictedScopes = [],
            Reason = $"{proposal.Rationale} (applied by {actor})",
        };

        await SaveAsync(moved, ct).ConfigureAwait(false);

        await ExecuteAsync(
            "UPDATE development_proposal SET status = @applied, approval_ref = @approval WHERE id = @id;",
            ct,
            ("@applied", ProposalStatus.Applied),
            ("@approval", (object?)approvalRef ?? DBNull.Value), ("@id", proposalId))
            .ConfigureAwait(false);

        // Visible, per rule 4. A change in how much Aurora does unasked belongs in the record a
        // person reads, not only in a table they would have to know to look at.
        await _audit.AppendAsync(
            new AuditEntry(
                actor, actor, "development.transition",
                Hashing.Sha256Hex($"{proposal.FromStageId}->{proposal.ToStageId}"),
                "completed", Risk: forward ? "Medium" : "Low", Via: "operator",
                Decision: proposal.ToStageId, PolicyIds: null, Reason: proposal.Rationale),
            ct).ConfigureAwait(false);

        // Rule 4 asks for visible. The audit journal is the record; the bus is how the panel finds
        // out without polling for it.
        await _bus.PublishAsync(
            new OutboxWrite(
                EventCatalogue.DevelopmentStageChanged, 1, EventCatalogue.Producers.Development,
                Guid.NewGuid().ToString("N"), Sensitivity.Private,
                AggregateRef: $"mind/{proposal.MindId}",
                PayloadJson: AuroraJson.Serialize(
                    new
                    {
                        from = proposal.FromStageId,
                        to = proposal.ToStageId,
                        autonomy = forward ? "grew" : "shrank",
                    }),
                IdempotencyKey: $"development:{proposal.Id}"),
            ct).ConfigureAwait(false);

        return moved;
    }

    public async Task<DevelopmentState> RestrictAsync(
        string mindId, string scope, string incidentRef, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(incidentRef))
        {
            throw new DevelopmentException("A restriction names its scope and the incident behind it.");
        }

        DevelopmentState state = await CurrentAsync(mindId, ct).ConfigureAwait(false);

        // Limit case: reduce the affected scope where possible, preserving evidence. The stage
        // itself does not move — pulling everything back for one failure discards what was earned
        // everywhere else, and the evidence stays either way.
        DevelopmentState restricted = state with
        {
            Status = DevelopmentStatus.Restricted,
            RestrictedScopes = state.RestrictedScopes
                .Append(scope).Distinct(StringComparer.Ordinal).ToList(),
            AssessmentAtUtc = Iso(_clock.UtcNow),
            Reason = $"{reason} ({incidentRef})",
        };

        await SaveAsync(restricted, ct).ConfigureAwait(false);
        return restricted;
    }

    public async Task<bool> WantsConfirmationAsync(
        string mindId, CapabilityDescriptor capability, CancellationToken ct)
    {
        DevelopmentState state = await CurrentAsync(mindId, ct).ConfigureAwait(false);
        DevelopmentStage stage = StageOf(state.CurrentStageId);

        if (state.Status == DevelopmentStatus.Paused)
        {
            return true;
        }

        // A restriction is scoped. A failure sending mail is a reason to confirm mail, not a reason
        // to confirm reading the clock.
        if (state.Status == DevelopmentStatus.Restricted
            && state.RestrictedScopes.Any(s =>
                capability.ActionId.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // The ceiling. Above it, development wants a confirmation on top of whatever policy already
        // requires — and below it, development is simply silent, which is not the same as consent.
        return capability.Risk > stage.AutonomyCeiling;
    }

    public async Task<DevelopmentState> CurrentAsync(string mindId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT mind_id, current_stage_id, evidence_refs, assessment_at_utc, status,
                   restricted_scopes, reason
              FROM development_state WHERE mind_id = @mind;
            """;
        command.Parameters.AddWithValue("@mind", mindId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new DevelopmentState(
                reader.GetString(0), reader.GetString(1), Lines(reader.GetString(2)),
                reader.GetString(3), reader.GetString(4), Lines(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6));
        }

        // A new instance starts supervised. Beginning anywhere else would be granting confidence
        // that nothing has been shown to deserve.
        return new DevelopmentState(
            mindId, _profile.Stages[0].Id, [], Iso(_clock.UtcNow),
            DevelopmentStatus.Probation, [], "a new instance starts supervised");
    }

    public async Task<IReadOnlyList<DevelopmentProposal>> ProposalsAsync(
        string mindId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, mind_id, from_stage_id, to_stage_id, evidence_refs, rationale,
                   proposed_at_utc, status, approval_ref
              FROM development_proposal WHERE mind_id = @mind ORDER BY proposed_at_utc;
            """;
        command.Parameters.AddWithValue("@mind", mindId);

        var proposals = new List<DevelopmentProposal>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            proposals.Add(Read(reader));
        }

        return proposals;
    }

    // ---- evidence ----

    /// <summary>
    /// Counts what actually happened, per risk level.
    /// </summary>
    /// <remarks>
    /// Read from the audit journal, which is the one record that cannot be talked into saying
    /// something else. Successes and failures are counted separately at each level and never
    /// summed across them — twenty successful clock readings are not an argument about writing
    /// files, and adding them up is exactly how they would become one.
    /// </remarks>
    private async Task<IReadOnlyList<ReliabilityEvidence>> GatherAsync(CancellationToken ct)
    {
        var byRisk = new Dictionary<RiskLevel, (int Successes, int Failures, List<string> Refs)>();

        long cursor = 0;
        while (true)
        {
            IReadOnlyList<AuditRecordView> page =
                await _audit.QueryAsync(cursor, 200, ct).ConfigureAwait(false);

            if (page.Count == 0)
            {
                break;
            }

            foreach (AuditRecordView record in page)
            {
                if (!_registry.TryGet(record.ActionId, out ICapability? capability))
                {
                    continue;
                }

                RiskLevel risk = capability.Descriptor.Risk;
                (int successes, int failures, List<string> refs) =
                    byRisk.TryGetValue(risk, out var current) ? current : (0, 0, []);

                if (record.Outcome == "completed")
                {
                    successes++;
                    refs.Add(record.RecordId);
                }
                else if (record.Outcome is "failed" or "unknown")
                {
                    failures++;
                    refs.Add(record.RecordId);
                }

                byRisk[risk] = (successes, failures, refs);
            }

            cursor = page[^1].Sequence;
        }

        return byRisk
            .Select(pair => new ReliabilityEvidence(
                pair.Key, pair.Value.Successes, pair.Value.Failures, pair.Value.Refs))
            .OrderBy(e => e.Risk)
            .ToList();
    }

    // ---- plumbing ----

    private DevelopmentAssessment Assessment(
        string mindId, DevelopmentState state, string? next,
        IReadOnlyList<ReliabilityEvidence> evidence, bool ready, IReadOnlyList<string> missing) =>
        new(mindId, state.CurrentStageId, next, evidence, ready, missing, Iso(_clock.UtcNow));

    private DevelopmentStage StageOf(string stageId) =>
        _profile.Stages.FirstOrDefault(s => s.Id == stageId)
        ?? throw new DevelopmentException($"Unknown stage '{stageId}'.");

    private int IndexOf(string stageId) =>
        _profile.Stages.ToList().FindIndex(s => s.Id == stageId);

    private DevelopmentStage? NextAfter(string stageId)
    {
        var index = IndexOf(stageId);
        return index >= 0 && index + 1 < _profile.Stages.Count ? _profile.Stages[index + 1] : null;
    }

    private async Task<DevelopmentProposal> ProposalAsync(string proposalId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, mind_id, from_stage_id, to_stage_id, evidence_refs, rationale,
                   proposed_at_utc, status, approval_ref
              FROM development_proposal WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@id", proposalId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? Read(reader)
            : throw new DevelopmentException("Unknown proposal.");
    }

    private static DevelopmentProposal Read(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            Lines(reader.GetString(4)), reader.GetString(5), reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8));

    private Task SaveAsync(DevelopmentState state, CancellationToken ct) =>
        ExecuteAsync("""
            INSERT INTO development_state
                (mind_id, current_stage_id, evidence_refs, assessment_at_utc, status,
                 restricted_scopes, reason)
            VALUES (@mind, @stage, @evidence, @at, @status, @scopes, @reason)
            ON CONFLICT(mind_id) DO UPDATE SET
                current_stage_id = @stage, evidence_refs = @evidence, assessment_at_utc = @at,
                status = @status, restricted_scopes = @scopes, reason = @reason;
            """, ct,
            ("@mind", state.MindId), ("@stage", state.CurrentStageId),
            ("@evidence", string.Join('\n', state.EvidenceRefs)),
            ("@at", state.AssessmentAtUtc), ("@status", state.Status),
            ("@scopes", string.Join('\n', state.RestrictedScopes)),
            ("@reason", (object?)state.Reason ?? DBNull.Value));

    private async Task ExecuteAsync(
        string sql, CancellationToken ct, params (string Name, object Value)[] args)
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

    private static IReadOnlyList<string> Lines(string value) =>
        value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
