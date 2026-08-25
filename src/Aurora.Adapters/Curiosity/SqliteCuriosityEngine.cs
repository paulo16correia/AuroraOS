using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Curiosity;

/// <summary>
/// Governed curiosity (RFC 032): autonomy limited by rule, in its most literal form.
/// </summary>
/// <remarks>
/// Curiosity is where an assistant most easily turns into a collection machine, so this class is
/// defined by what it cannot reach. It answers only from an allowlist. It produces only a DRAFT
/// goal made of research. It takes no dependency on memory at all, because rule 4 says research
/// does not create knowledge and the cleanest way to enforce that is to have no way to write any.
/// </remarks>
public sealed class SqliteCuriosityEngine : ICuriosityEngine
{
    private readonly SqliteConnectionFactory _factory;
    private readonly IPlanner _planner;
    private readonly IResourceModel _resources;
    private readonly ISituationService _situation;
    private readonly INeedsService _needs;
    private readonly IClock _clock;

    public SqliteCuriosityEngine(
        SqliteConnectionFactory factory,
        IPlanner planner,
        IResourceModel resources,
        ISituationService situation,
        INeedsService needs,
        IClock clock)
    {
        _factory = factory;
        _planner = planner;
        _resources = resources;
        _situation = situation;
        _needs = needs;
        _clock = clock;
    }

    public async Task<IReadOnlyList<CuriosityProposal>> DetectAsync(
        CuriositySnapshot snapshot, CuriosityPolicy policy, CancellationToken ct)
    {
        var proposals = new List<CuriosityProposal>();

        foreach (KnowledgeGap gap in snapshot.Gaps)
        {
            // Not curious enough to be worth anyone's resources yet. Something seen once is not a
            // pattern, and something Aurora is already fairly sure of is not a gap.
            if (gap.TimesSeen < policy.MinTimesSeen || gap.Confidence > policy.MaxConfidenceToAsk)
            {
                continue;
            }

            var refusals = new List<string>();

            // Rule 1: an allowlist. A source that is not named is not permitted — there is no
            // "everything except" setting, because that is the setting people forget to narrow.
            if (!policy.AllowedSources.Contains(gap.Source, StringComparer.Ordinal))
            {
                refusals.Add(CuriosityRefusal.SourceNotPermitted);
            }

            if (Sensitivity.Rank(gap.SensitivityClass) > Sensitivity.Rank(policy.SensitivityCeiling))
            {
                refusals.Add(CuriosityRefusal.AboveSensitivityCeiling);
            }

            var status = refusals.Count > 0 ? CuriosityStatus.Rejected : CuriosityStatus.Candidate;

            CuriosityProposal? existing = await OpenForSubjectAsync(gap.SubjectRef, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                proposals.Add(existing);
                continue;
            }

            var proposal = new CuriosityProposal(
                Guid.NewGuid().ToString("N"), gap.Question, gap.RationaleRefs,
                ExpectedValue: Math.Clamp(gap.TimesSeen / 10.0 * (1 - gap.Confidence), 0, 1),
                Scope: gap.SubjectRef,
                AllowedSources: policy.AllowedSources,
                SensitivityLimit: policy.SensitivityCeiling,
                ResourceBudget: policy.MaxBudgetPerProposal,
                status,

                // Always. Even a permitted, public, cheap question is Aurora spending the owner's
                // resources on something the owner did not ask for.
                ApprovalRequired: true,
                ResultRefs: [], ReviewAtUtc: Iso(_clock.UtcNow + policy.Review),
                DetectedAtUtc: Iso(_clock.UtcNow), RefusalReasons: refusals);

            await InsertAsync(proposal, gap.SubjectRef, ct).ConfigureAwait(false);
            proposals.Add(proposal);
        }

        return proposals;
    }

    public async Task<DecisionOption> EvaluateAsync(
        string proposalId, SituationAssessment situation, ResourceBudget budget, CancellationToken ct)
    {
        CuriosityProposal proposal = await RequireAsync(proposalId, ct).ConfigureAwait(false);
        var blocking = new List<string>(proposal.RefusalReasons);

        // Rule 3, first clause: curiosity competes for real capacity as discretionary work, which
        // is the class that gives way before anything else.
        AdmissionResult admission = await _resources.AdmitAsync(
            $"curiosity/{proposalId}", WorkClass.Discretionary, proposal.ResourceBudget, budget, ct)
            .ConfigureAwait(false);

        if (admission.Decision == Admission.Allow)
        {
            // Only weighing it. Holding the slot while a decision is still being made would be
            // curiosity taking capacity for a question nobody has agreed to ask.
            await _resources.ReleaseAsync(admission.ReservationId!, "evaluated", ct).ConfigureAwait(false);
        }
        else
        {
            blocking.Add(CuriosityRefusal.NoResources);
        }

        // Rule 3, second clause: the moment. Research is internal, so it does not impose on the
        // person — but an emergency posture still stops it.
        AppropriatenessResult moment = _situation.IsAppropriate(
            WorkClass.Discretionary, imposesOnUser: false, situation);

        if (!moment.Appropriate)
        {
            blocking.Add(CuriosityRefusal.WrongMoment);
        }

        // Rule 3, third clause: what else is waiting. An open incident means the system is failing
        // to keep a promise it already made, and a question can wait for that.
        IReadOnlyList<Need> waiting = await _needs.RankAsync(ct).ConfigureAwait(false);
        if (waiting.Any(n => NeedKind.IsIncident(n.Kind)))
        {
            blocking.Add(CuriosityRefusal.OutrankedByNeeds);
        }

        return new DecisionOption(
            DecisionMode.Plan,
            $"Investigate: {proposal.Question}",
            ExpectedEffects: [],
            new OptionEvaluation(
                Relevance: proposal.ExpectedValue,
                HasEvidence: proposal.RationaleRefs.Count > 0,
                RiskLevel: RiskLevel.Low.ToString(),
                CostEstimate: proposal.ResourceBudget,
                Permitted: proposal.Status != CuriosityStatus.Rejected,
                Reversible: true),
            Prerequisites: proposal.ApprovalRequired ? ["the owner approves this question"] : [],
            BlockingReasons: blocking);
    }

    public async Task<CuriosityProposal> ScheduleAsync(
        string proposalId, string approvalRef, CancellationToken ct)
    {
        CuriosityProposal proposal = await RequireAsync(proposalId, ct).ConfigureAwait(false);

        if (proposal.Status == CuriosityStatus.Rejected)
        {
            throw new CuriosityException(
                $"This proposal was refused: {string.Join(", ", proposal.RefusalReasons)}.");
        }

        if (proposal.ApprovalRequired && string.IsNullOrWhiteSpace(approvalRef))
        {
            throw new CuriosityException("Investigating needs the owner's approval.");
        }

        if (proposal.GoalRef is not null)
        {
            return proposal;
        }

        // Rule 2, structurally: a DRAFT goal of RESEARCH tasks at LOW risk assigned to Aurora. There
        // is no path from here to a tool call, an account, a message or a purchase — not because
        // those are checked for, but because this is the only thing curiosity can build.
        Goal goal = await _planner.DraftAsync(
            new GoalRequest(
                Title: $"Investigate: {proposal.Question}",
                Outcome: $"the question is answered from {string.Join(", ", proposal.AllowedSources)}",
                OwnerId: NeedOwner.System,
                SuccessCriteria: [$"an answer is recorded, with its source, for {proposal.Scope}"],
                Assumptions:
                [
                    "raised by Aurora's own curiosity, not asked for by the owner",
                    $"answerable within {proposal.SensitivityLimit} material and "
                    + $"{proposal.ResourceBudget:F2} of budget",
                ],
                Priority: 5),
            ct).ConfigureAwait(false);

        await ExecuteAsync(
            "UPDATE curiosity_proposal SET status = @s, goal_ref = @g WHERE id = @id;", ct,
            ("@s", CuriosityStatus.Scheduled), ("@g", goal.Id), ("@id", proposalId))
            .ConfigureAwait(false);

        return proposal with { Status = CuriosityStatus.Scheduled, GoalRef = goal.Id };
    }

    public async Task<CuriosityProposal> RecordResultAsync(
        string proposalId, string observationRef, CancellationToken ct)
    {
        CuriosityProposal proposal = await RequireAsync(proposalId, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(observationRef))
        {
            throw new CuriosityException("A result is recorded against the observation that produced it.");
        }

        var results = proposal.ResultRefs.Append(observationRef).Distinct(StringComparer.Ordinal).ToList();

        // LEARNED means "the question was investigated and the answer is on file", not "Aurora now
        // believes this". Turning an observation into a memory is a separate act with its own
        // provenance and its own anchor (LAW-001, LAW-004), and it does not happen here.
        await ExecuteAsync(
            "UPDATE curiosity_proposal SET status = @s, result_refs = @r WHERE id = @id;", ct,
            ("@s", CuriosityStatus.Learned), ("@r", string.Join('\n', results)), ("@id", proposalId))
            .ConfigureAwait(false);

        return proposal with { Status = CuriosityStatus.Learned, ResultRefs = results };
    }

    public Task<int> ExpireDueAsync(CancellationToken ct) =>
        ExecuteAsync("""
            UPDATE curiosity_proposal
               SET status = @expired
             WHERE review_at_utc IS NOT NULL AND review_at_utc <= @now
               AND status IN (@candidate, @approved);
            """, ct,
            ("@expired", CuriosityStatus.Expired), ("@now", Iso(_clock.UtcNow)),
            ("@candidate", CuriosityStatus.Candidate), ("@approved", CuriosityStatus.Approved));

    public async Task<CuriosityProposal?> GetAsync(string proposalId, CancellationToken ct)
    {
        IReadOnlyList<CuriosityProposal> found = await ReadAsync(
            $"{Select} WHERE id = @id;", ct, ("@id", proposalId)).ConfigureAwait(false);

        return found.Count == 0 ? null : found[0];
    }

    public Task<IReadOnlyList<CuriosityProposal>> ListAsync(string? status, CancellationToken ct) =>
        status is null
            ? ReadAsync($"{Select} ORDER BY expected_value DESC;", ct)
            : ReadAsync($"{Select} WHERE status = @s ORDER BY expected_value DESC;", ct, ("@s", status));

    // ---- plumbing ----

    private async Task<CuriosityProposal?> OpenForSubjectAsync(string subjectRef, CancellationToken ct)
    {
        IReadOnlyList<CuriosityProposal> open = await ReadAsync($"""
            {Select} WHERE subject_ref = @s AND status NOT IN (@expired, @learned)
             ORDER BY detected_at_utc DESC
            """, ct,
            ("@s", subjectRef), ("@expired", CuriosityStatus.Expired),
            ("@learned", CuriosityStatus.Learned)).ConfigureAwait(false);

        return open.Count == 0 ? null : open[0];
    }

    private async Task<CuriosityProposal> RequireAsync(string proposalId, CancellationToken ct) =>
        await GetAsync(proposalId, ct).ConfigureAwait(false)
        ?? throw new CuriosityException("Unknown proposal.");

    private Task InsertAsync(CuriosityProposal p, string subjectRef, CancellationToken ct) =>
        ExecuteAsync("""
            INSERT INTO curiosity_proposal
                (id, question, rationale_refs, expected_value, scope, allowed_sources,
                 sensitivity_limit, resource_budget, status, approval_required, result_refs,
                 review_at_utc, detected_at_utc, refusal_reasons, goal_ref, subject_ref)
            VALUES (@id, @q, @rationale, @value, @scope, @sources, @sensitivity, @budget, @status,
                    @approval, '', @review, @at, @refusals, NULL, @subject);
            """, ct,
            ("@id", p.Id), ("@q", p.Question), ("@rationale", string.Join('\n', p.RationaleRefs)),
            ("@value", p.ExpectedValue), ("@scope", p.Scope),
            ("@sources", string.Join('\n', p.AllowedSources)),
            ("@sensitivity", p.SensitivityLimit), ("@budget", p.ResourceBudget),
            ("@status", p.Status), ("@approval", p.ApprovalRequired ? 1 : 0),
            ("@review", (object?)p.ReviewAtUtc ?? DBNull.Value), ("@at", p.DetectedAtUtc),
            ("@refusals", string.Join('\n', p.RefusalReasons)), ("@subject", subjectRef));

    private const string Select = """
        SELECT id, question, rationale_refs, expected_value, scope, allowed_sources,
               sensitivity_limit, resource_budget, status, approval_required, result_refs,
               review_at_utc, detected_at_utc, refusal_reasons, goal_ref
          FROM curiosity_proposal
        """;

    private async Task<IReadOnlyList<CuriosityProposal>> ReadAsync(
        string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((var name, var value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var proposals = new List<CuriosityProposal>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            proposals.Add(new CuriosityProposal(
                reader.GetString(0), reader.GetString(1), Lines(reader.GetString(2)),
                reader.GetDouble(3), reader.GetString(4), Lines(reader.GetString(5)),
                reader.GetString(6), reader.GetDouble(7), reader.GetString(8),
                reader.GetInt32(9) == 1, Lines(reader.GetString(10)),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.GetString(12), Lines(reader.GetString(13)),
                reader.IsDBNull(14) ? null : reader.GetString(14)));
        }

        return proposals;
    }

    private async Task<int> ExecuteAsync(
        string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((var name, var value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> Lines(string value) =>
        value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
