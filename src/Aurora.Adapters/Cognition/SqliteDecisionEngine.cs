using System.Globalization;
using System.Text.Json;
using Aurora.Adapters.Persistence;
using Aurora.Core;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Cognition;

/// <summary>Chooses how to act, separately from writing the words (RFC 022).</summary>
public sealed class SqliteDecisionEngine : IDecisionEngine
{
    /// <summary>Below this, the engine says so rather than letting the number speak for itself.</summary>
    private const double ConfidentEnough = 0.7;

    /// <summary>
    /// How long an effectful decision stands when nobody set a deadline.
    /// </summary>
    /// <remarks>
    /// Long enough for an approval to be answered by somebody who stepped away, short enough that a
    /// decision found the next morning has lapsed rather than still being live.
    /// </remarks>
    private static readonly TimeSpan EffectfulDecisionWindow = TimeSpan.FromHours(1);

    private readonly SqliteConnectionFactory _factory;
    private readonly IConstitution _constitution;
    private readonly IClock _clock;

    public SqliteDecisionEngine(
        SqliteConnectionFactory factory, IConstitution constitution, IClock clock)
    {
        _factory = factory;
        _constitution = constitution;
        _clock = clock;
    }

    public async Task<Decision> EvaluateAsync(
        DecisionThought thought, DecisionContext context, CancellationToken ct)
    {
        if (thought.Options.Count == 0)
        {
            throw new DecisionException("There is nothing to decide between.");
        }

        var uncertainty = new List<string>();
        var viable = new List<DecisionOption>();

        foreach (DecisionOption option in thought.Options)
        {
            if (option.BlockingReasons.Count > 0)
            {
                continue;
            }

            // Limit case: the motor is unavailable, so tools are blocked and only approved static
            // responses remain. A blocked tool is not silently swapped for a different effect.
            if (!context.MotorAvailable && option.Mode == DecisionMode.ToolCall)
            {
                uncertainty.Add("motor unavailable; tool options were not considered");
                continue;
            }

            // Rule 3: silence needs one of the four permitted reasons, and the channel must allow
            // it. There is no reason for hiding a failure, and a failing cycle may never be silent.
            if (option.Mode == DecisionMode.Silent
                && (thought.ReportingFailure
                    || !SilenceReason.IsAllowed(option.SilenceReasonCode)
                    || !context.AllowedSilenceReasons.Contains(
                        option.SilenceReasonCode!, StringComparer.Ordinal)))
            {
                uncertainty.Add("silence was not available for this channel or outcome");
                continue;
            }

            // Never escalate privilege: an option the evaluation says is not permitted is dropped,
            // not granted because it scored well.
            if (!option.Evaluation.Permitted)
            {
                continue;
            }

            viable.Add(option);
        }

        DecisionOption selected;
        var mode = thought.Confidence >= 0.8 && thought.EvidenceRefs.Count == 0
            ? DecisionMode.Ask
            : null;

        if (mode is not null)
        {
            // Limit case: high confidence with no source. Reduce to asking, and say why — the one
            // thing that must not happen is treating an unsourced claim as ground truth.
            uncertainty.Add("high confidence with no evidence; reduced to ASK");
            selected = viable.FirstOrDefault(o => o.Mode == DecisionMode.Ask)
                ?? Fallback(DecisionMode.Ask, "No evidence supports the confident option.");
        }
        else if (viable.Count == 0)
        {
            selected = Fallback(DecisionMode.Ask, "No option was viable; asking instead of acting.");
            uncertainty.Add("no viable option");
        }
        else
        {
            selected = Choose(viable);
        }

        // Article 2: middling confidence is material uncertainty, and a decision that carries it
        // silently is one that reads as surer than it is. Said here rather than checked away — the
        // Constitution verifies that it was declared; it is this engine's job to declare it.
        if (thought.Confidence < ConfidentEnough && uncertainty.Count == 0)
        {
            uncertainty.Add(
                $"confidence is {thought.Confidence.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        var alternatives = thought.Options.Where(o => !ReferenceEquals(o, selected)).ToList();

        // Article 8: a decision with an effect is time-limited. Where the caller set no deadline
        // this bounds it anyway, because an effectful decision that never expires is a standing
        // permission that nobody granted (RFC 022 rule 4).
        var expiry = context.DeadlineAtUtc
            ?? (DecisionMode.HasExternalEffect(selected.Mode)
                ? Iso(_clock.UtcNow + EffectfulDecisionWindow)
                : null);

        var decision = new Decision(
            Guid.NewGuid().ToString("N"), thought.CycleId, selected.Mode, thought.ObjectiveRef,
            selected, alternatives, thought.EvidenceRefs, uncertainty, thought.RiskLevel,
            thought.Confidence, PolicyDecisionIds: [],
            ApprovalRequired: DecisionMode.HasExternalEffect(selected.Mode),
            expiry, DecisionState.Proposed);

        await SaveAsync(decision, ct).ConfigureAwait(false);
        return decision;
    }

    public async Task<Decision> CommitAsync(
        string decisionId, IReadOnlyList<PolicyResult> policyResults, CancellationToken ct)
    {
        Decision decision = await GetAsync(decisionId, ct).ConfigureAwait(false)
            ?? throw new DecisionException("Unknown decision.");

        if (decision.Status != DecisionState.Proposed)
        {
            throw new DecisionException($"Only a PROPOSED decision is committed; this is {decision.Status}.");
        }

        if (IsExpired(decision))
        {
            await SetStatusAsync(decisionId, DecisionState.Expired, ct).ConfigureAwait(false);
            throw new DecisionException("The decision expired before it was committed.");
        }

        // Rule 2: a TOOL_CALL reaches outside Aurora, so it needs an explicit allow and, where the
        // decision says approval is required, a satisfied approval. No allow, no effect.
        if (DecisionMode.HasExternalEffect(decision.Mode))
        {
            if (policyResults.Count == 0 || !policyResults.All(r => r.Allowed))
            {
                throw new DecisionException(
                    "A TOOL_CALL cannot be committed without an allowing policy decision.");
            }

            if (decision.ApprovalRequired && !policyResults.All(r => r.ApprovalSatisfied))
            {
                throw new DecisionException(
                    "A TOOL_CALL that requires approval cannot be committed until it is satisfied.");
            }
        }

        Decision committed = decision with
        {
            Status = DecisionState.Committed,
            PolicyDecisionIds = policyResults.Select(r => r.PolicyDecisionId).ToList(),
        };

        // RFC 035 rule 2: a high-risk decision keeps a constitutional assessment. Assessed on the
        // committed shape rather than the proposed one, because the policy decisions it cites are
        // part of what Article 6 is about, and they do not exist until here.
        if (IsHighRisk(committed))
        {
            ConstitutionalAssessment assessment =
                _constitution.Assess(committed, Iso(_clock.UtcNow));

            if (assessment.Result == ConstitutionalResult.Fail)
            {
                // Not a warning. An Article is not a preference to be weighed against getting the
                // job done, which is the entire reason RFC 035 sits above the policies.
                throw new DecisionException(
                    "The decision contradicts the Constitution: "
                    + string.Join("; ", assessment.Conflicts.Select(c => $"{c.Article} — {c.Detail}")));
            }

            await SaveAssessmentAsync(assessment, ct).ConfigureAwait(false);
            committed = committed with { ConstitutionalAssessmentRef = assessment.Id };
        }

        await SaveAsync(committed, ct).ConfigureAwait(false);
        return committed;
    }

    /// <summary>
    /// Which decisions RFC 035 rule 2 calls high risk.
    /// </summary>
    /// <remarks>
    /// Anything that reaches outside Aurora, and anything the engine itself rated HIGH or CRITICAL.
    /// A decision that stays inside and was rated low is not exempt from the Articles — it is
    /// exempt from having to carry the paperwork proving it.
    /// </remarks>
    private static bool IsHighRisk(Decision decision) =>
        DecisionMode.HasExternalEffect(decision.Mode)
        || string.Equals(decision.RiskLevel, nameof(RiskLevel.High), StringComparison.OrdinalIgnoreCase)
        || string.Equals(decision.RiskLevel, nameof(RiskLevel.Critical), StringComparison.OrdinalIgnoreCase);

    /// <summary>The assessment a committed decision cites, read back for the panel and the audit.</summary>
    public async Task<ConstitutionalAssessment?> AssessmentAsync(
        string assessmentId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, subject_ref, articles_checked, result, conflicts_json, evidence_refs, " +
            "assessed_at_utc FROM constitutional_assessment WHERE id = @id;";

        command.Parameters.AddWithValue("@id", assessmentId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new ConstitutionalAssessment(
            reader.GetString(0), reader.GetString(1),
            reader.GetString(2).Split(',', StringSplitOptions.RemoveEmptyEntries),
            reader.GetString(3),
            AuroraJson.Deserialize<List<ArticleFinding>>(reader.GetString(4)) ?? [],
            reader.GetString(5).Split(',', StringSplitOptions.RemoveEmptyEntries),
            reader.GetString(6));
    }

    private async Task SaveAssessmentAsync(ConstitutionalAssessment assessment, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO constitutional_assessment (id, subject_ref, articles_checked, result, " +
            "conflicts_json, evidence_refs, assessed_at_utc) " +
            "VALUES (@id, @subject, @articles, @result, @conflicts, @evidence, @at);";

        command.Parameters.AddWithValue("@id", assessment.Id);
        command.Parameters.AddWithValue("@subject", assessment.SubjectRef);
        command.Parameters.AddWithValue("@articles", string.Join(',', assessment.ArticlesChecked));
        command.Parameters.AddWithValue("@result", assessment.Result);
        command.Parameters.AddWithValue("@conflicts", AuroraJson.Serialize(assessment.Conflicts));
        command.Parameters.AddWithValue("@evidence", string.Join(',', assessment.EvidenceRefs));
        command.Parameters.AddWithValue("@at", assessment.AssessedAtUtc);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<Decision> InvalidateAsync(string decisionId, string reason, CancellationToken ct)
    {
        Decision decision = await GetAsync(decisionId, ct).ConfigureAwait(false)
            ?? throw new DecisionException("Unknown decision.");

        if (decision.Status == DecisionState.Executed)
        {
            // Superseding must happen before the effect. Afterwards the honest record is that it
            // ran, and a new decision is needed rather than a rewrite of the old one.
            throw new DecisionException("An executed decision is not superseded; make a new one.");
        }

        Decision superseded = decision with
        {
            Status = DecisionState.Superseded,
            Uncertainty = decision.Uncertainty.Append($"superseded: {reason}").ToList(),
        };

        await SaveAsync(superseded, ct).ConfigureAwait(false);
        return superseded;
    }

    public async Task<int> ExpireDueAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE decision SET status = @expired
             WHERE status = @proposed AND expiry_at_utc IS NOT NULL AND expiry_at_utc <= @now;
            """;
        command.Parameters.AddWithValue("@expired", DecisionState.Expired);
        command.Parameters.AddWithValue("@proposed", DecisionState.Proposed);
        command.Parameters.AddWithValue("@now", Iso(_clock.UtcNow));

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<Decision?> GetAsync(string decisionId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, cycle_id, mode, objective_ref, selected_option_json, alternatives_json,
                   evidence_refs, uncertainty, risk_level, confidence, policy_decision_ids,
                   approval_required, expiry_at_utc, status, constitutional_assessment_ref
              FROM decision WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@id", decisionId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new Decision(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            JsonSerializer.Deserialize<DecisionOption>(reader.GetString(4))!,
            JsonSerializer.Deserialize<List<DecisionOption>>(reader.GetString(5))!,
            Split(reader.GetString(6)), reader.GetString(7).Split('\n', StringSplitOptions.RemoveEmptyEntries),
            reader.GetString(8), reader.GetDouble(9), Split(reader.GetString(10)),
            reader.GetInt32(11) == 1, reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14));
    }

    /// <summary>
    /// Two equivalent options resolve toward the smaller footprint: less external effect first,
    /// then lower cost. A tie that survives both becomes a question rather than a coin toss.
    /// </summary>
    private static DecisionOption Choose(IReadOnlyList<DecisionOption> viable)
    {
        var ordered = viable
            .OrderBy(o => DecisionMode.HasExternalEffect(o.Mode) ? 1 : 0)
            .ThenBy(o => o.Evaluation.CostEstimate)
            .ThenBy(o => o.Evaluation.Reversible ? 0 : 1)
            .ThenBy(o => o.Mode, StringComparer.Ordinal)
            .ToList();

        DecisionOption best = ordered[0];
        var tied = ordered.Count > 1
            && DecisionMode.HasExternalEffect(ordered[1].Mode) == DecisionMode.HasExternalEffect(best.Mode)
            && Math.Abs(ordered[1].Evaluation.CostEstimate - best.Evaluation.CostEstimate) < double.Epsilon
            && ordered[1].Evaluation.Reversible == best.Evaluation.Reversible;

        return tied && DecisionMode.HasExternalEffect(best.Mode)
            ? Fallback(DecisionMode.Ask, "Two equivalent options with external effect; asking for a preference.")
            : best;
    }

    private static DecisionOption Fallback(string mode, string rationale) => new(
        mode, rationale, ExpectedEffects: [],
        new OptionEvaluation(0, false, "LOW", 0, Permitted: true, Reversible: true),
        Prerequisites: [], BlockingReasons: []);

    private bool IsExpired(Decision decision) =>
        decision.ExpiryAtUtc is { } expiry
        && DateTimeOffset.TryParse(
            expiry, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
        && at <= _clock.UtcNow;

    private async Task SaveAsync(Decision d, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO decision
                (id, cycle_id, mode, objective_ref, selected_option_json, alternatives_json,
                 evidence_refs, uncertainty, risk_level, confidence, policy_decision_ids,
                 approval_required, expiry_at_utc, status, constitutional_assessment_ref)
            VALUES (@id, @c, @mode, @obj, @sel, @alt, @ev, @unc, @risk, @conf, @pol, @appr, @exp,
                    @status, @assessment)
            ON CONFLICT(id) DO UPDATE SET
                mode = excluded.mode, selected_option_json = excluded.selected_option_json,
                alternatives_json = excluded.alternatives_json, uncertainty = excluded.uncertainty,
                policy_decision_ids = excluded.policy_decision_ids, status = excluded.status,
                constitutional_assessment_ref = excluded.constitutional_assessment_ref;
            """;
        command.Parameters.AddWithValue("@id", d.Id);
        command.Parameters.AddWithValue(
            "@assessment", (object?)d.ConstitutionalAssessmentRef ?? DBNull.Value);
        command.Parameters.AddWithValue("@c", d.CycleId);
        command.Parameters.AddWithValue("@mode", d.Mode);
        command.Parameters.AddWithValue("@obj", (object?)d.ObjectiveRef ?? DBNull.Value);
        command.Parameters.AddWithValue("@sel", JsonSerializer.Serialize(d.SelectedOption));
        command.Parameters.AddWithValue("@alt", JsonSerializer.Serialize(d.AlternativesConsidered));
        command.Parameters.AddWithValue("@ev", string.Join(',', d.EvidenceRefs));
        command.Parameters.AddWithValue("@unc", string.Join('\n', d.Uncertainty));
        command.Parameters.AddWithValue("@risk", d.RiskLevel);
        command.Parameters.AddWithValue("@conf", d.Confidence);
        command.Parameters.AddWithValue("@pol", string.Join(',', d.PolicyDecisionIds));
        command.Parameters.AddWithValue("@appr", d.ApprovalRequired ? 1 : 0);
        command.Parameters.AddWithValue("@exp", (object?)d.ExpiryAtUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("@status", d.Status);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task SetStatusAsync(string id, string status, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE decision SET status = @s WHERE id = @id;";
        command.Parameters.AddWithValue("@s", status);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries);

    private static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
