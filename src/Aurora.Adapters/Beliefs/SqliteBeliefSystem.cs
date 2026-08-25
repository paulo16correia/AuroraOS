using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Beliefs;

/// <summary>
/// The belief system (RFC 028): useful patterns that are never mistaken for facts.
/// </summary>
/// <remarks>
/// Held apart from memory deliberately. A memory is something that was recorded; a belief is a
/// pattern Aurora thinks it sees across them. Keeping them in different tables with different rules
/// is what lets Aurora act on a pattern without pretending it is reality, and stops a transitory
/// inference hardening into a permanent fact by sitting in the same place as one.
/// </remarks>
public sealed class SqliteBeliefSystem : IBeliefSystem
{
    /// <summary>
    /// Reference prefixes that are the model talking about itself.
    /// </summary>
    /// <remarks>
    /// RFC 028 rule 1 ends with "the model alone is not evidence", and the only way to hold that is
    /// to be able to recognise the model's own output when it is offered as support. A belief whose
    /// entire case is something a reasoner said is a belief with no case at all.
    /// </remarks>
    private static readonly string[] ModelPrefixes =
        ["model/", "reasoner/", "inference/", "llm/", "thought/", "deliberation/"];

    private readonly SqliteConnectionFactory _factory;
    private readonly BeliefPolicy _policy;
    private readonly IClock _clock;

    public SqliteBeliefSystem(SqliteConnectionFactory factory, BeliefPolicy policy, IClock clock)
    {
        _factory = factory;
        _policy = policy;
        _clock = clock;
    }

    public async Task<Belief> ProposeAsync(
        BeliefCandidate candidate, IReadOnlyList<string> evidenceRefs, CancellationToken ct)
    {
        if (!BeliefBasis.IsKnown(candidate.Basis) || !DecisionImpact.IsKnown(candidate.DecisionImpact))
        {
            throw new BeliefException("A belief needs a known basis and decision impact.");
        }

        if (evidenceRefs.Count == 0)
        {
            throw new BeliefException("A belief states what supports it; this one states nothing.");
        }

        // Rule 1, and the sharpest line in the RFC: the model alone is not evidence. A pattern the
        // reasoner asserts about its own reasoning is not a second opinion, it is the same one.
        if (evidenceRefs.All(IsModelOutput))
        {
            throw new BeliefException(
                "Every reference here is the model's own output. The model is not evidence for itself.");
        }

        DateTimeOffset now = _clock.UtcNow;
        var confidence = Math.Clamp(candidate.Confidence, 0, 1);

        // Insufficient support stays CANDIDATE, and nothing material is personalised from those.
        var status = confidence >= _policy.CandidateThreshold
            ? BeliefStatus.Active
            : BeliefStatus.Candidate;

        var belief = new Belief(
            Guid.NewGuid().ToString("N"), candidate.SubjectRef, candidate.Predicate,
            candidate.ObjectJson, candidate.ScopeJson, confidence,
            evidenceRefs, EvidenceAgainstRefs: [], candidate.Basis, status,
            Iso(now), Iso(now + _policy.ReviewAfter), Iso(now), candidate.DecisionImpact);

        await ExecuteAsync("""
            INSERT INTO belief
                (id, subject_ref, predicate, object_json, scope_json, confidence,
                 evidence_for_refs, evidence_against_refs, basis, status, valid_from_utc,
                 review_at_utc, last_evaluated_at_utc, decision_impact)
            VALUES (@id, @subject, @predicate, @object, @scope, @confidence, @for, '', @basis,
                    @status, @from, @review, @evaluated, @impact);
            """, ct,
            ("@id", belief.Id), ("@subject", belief.SubjectRef), ("@predicate", belief.Predicate),
            ("@object", belief.ObjectJson), ("@scope", belief.ScopeJson),
            ("@confidence", belief.Confidence), ("@for", string.Join('\n', evidenceRefs)),
            ("@basis", belief.Basis), ("@status", belief.Status), ("@from", belief.ValidFromUtc),
            ("@review", belief.ReviewAtUtc), ("@evaluated", belief.LastEvaluatedAtUtc),
            ("@impact", belief.DecisionImpact)).ConfigureAwait(false);

        return belief;
    }

    public async Task<BeliefUpdate> ObserveAsync(
        string beliefId, string observationRef, double deltaConfidence, string reason, CancellationToken ct)
    {
        Belief belief = await RequireAsync(beliefId, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(observationRef) || string.IsNullOrWhiteSpace(reason))
        {
            throw new BeliefException("An update names what was observed and why it moved the belief.");
        }

        DateTimeOffset now = _clock.UtcNow;
        var confidence = Math.Clamp(belief.Confidence + deltaConfidence, 0, 1);

        // Evidence goes on the side it argues for. A failed prediction attaches counter-evidence
        // and is re-evaluated; it is never silently erased, because the record of having believed
        // something wrong is the part worth keeping.
        var supporting = deltaConfidence >= 0
            ? belief.EvidenceForRefs.Append(observationRef).Distinct(StringComparer.Ordinal).ToList()
            : belief.EvidenceForRefs.ToList();

        var against = deltaConfidence < 0
            ? belief.EvidenceAgainstRefs.Append(observationRef).Distinct(StringComparer.Ordinal).ToList()
            : belief.EvidenceAgainstRefs.ToList();

        var status = belief.Status == BeliefStatus.Candidate && confidence >= _policy.CandidateThreshold
            ? BeliefStatus.Active
            : belief.Status;

        await ExecuteAsync("""
            UPDATE belief
               SET confidence = @c, evidence_for_refs = @for, evidence_against_refs = @against,
                   status = @status, last_evaluated_at_utc = @at, review_at_utc = @review
             WHERE id = @id;
            """, ct,
            ("@c", confidence), ("@for", string.Join('\n', supporting)),
            ("@against", string.Join('\n', against)), ("@status", status),
            ("@at", Iso(now)), ("@review", Iso(now + _policy.ReviewAfter)), ("@id", beliefId))
            .ConfigureAwait(false);

        var update = new BeliefUpdate(
            Guid.NewGuid().ToString("N"), beliefId, observationRef, deltaConfidence, reason, Iso(now));

        await ExecuteAsync("""
            INSERT INTO belief_update
                (id, belief_id, observation_ref, delta_confidence, reason, applied_at_utc)
            VALUES (@id, @belief, @obs, @delta, @reason, @at);
            """, ct,
            ("@id", update.Id), ("@belief", beliefId), ("@obs", observationRef),
            ("@delta", deltaConfidence), ("@reason", reason), ("@at", update.AppliedAtUtc))
            .ConfigureAwait(false);

        return update;
    }

    public async Task<BeliefSupport> SupportAsync(
        string subjectRef, string purpose, MemoryAccessContext access, CancellationToken ct)
    {
        if (!BeliefPurpose.IsKnown(purpose))
        {
            throw new BeliefException($"Unknown purpose '{purpose}'.");
        }

        IReadOnlyList<Belief> usable = await ReadAsync("""
            SELECT id, subject_ref, predicate, object_json, scope_json, confidence,
                   evidence_for_refs, evidence_against_refs, basis, status, valid_from_utc,
                   review_at_utc, last_evaluated_at_utc, decision_impact
              FROM belief
             WHERE subject_ref = @subject AND status = @active AND confidence >= @floor
             ORDER BY confidence DESC;
            """, ct,
            ("@subject", subjectRef), ("@active", BeliefStatus.Active),
            ("@floor", _policy.MinimumUsableConfidence)).ConfigureAwait(false);

        // Rule 2. The answer is the same whatever the beliefs say and however confident they are,
        // and it travels with them so a caller cannot obtain the beliefs without also obtaining
        // the fact that they are not enough.
        if (BeliefPurpose.IsHighRisk(purpose))
        {
            return new BeliefSupport(
                usable, MayBeSoleBasis: false,
                $"{purpose} needs more than a pattern Aurora noticed. These may inform the "
                + "decision and may not carry it.");
        }

        return usable.Count == 0
            ? new BeliefSupport([], false, "nothing is believed about this with enough confidence")
            : new BeliefSupport(usable, true, $"{usable.Count} belief(s) above the usable threshold");
    }

    public async Task<Belief> ChallengeAsync(
        string beliefId, string evidenceRef, string reason, CancellationToken ct)
    {
        Belief belief = await RequireAsync(beliefId, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(evidenceRef))
        {
            throw new BeliefException("A challenge names the evidence that contradicts the belief.");
        }

        var against = belief.EvidenceAgainstRefs
            .Append(evidenceRef).Distinct(StringComparer.Ordinal).ToList();

        // Confidence is deliberately left where it is. RFC 028's limit case says contradiction is
        // answered by narrowing or separating scope, not by splitting the difference — averaging
        // turns two incompatible observations into one lukewarm claim that describes neither.
        await ExecuteAsync("""
            UPDATE belief
               SET status = @challenged, evidence_against_refs = @against, last_evaluated_at_utc = @at
             WHERE id = @id;
            """, ct,
            ("@challenged", BeliefStatus.Challenged), ("@against", string.Join('\n', against)),
            ("@at", Iso(_clock.UtcNow)), ("@id", beliefId)).ConfigureAwait(false);

        await RecordUpdateAsync(beliefId, evidenceRef, 0, $"challenged: {reason}", ct)
            .ConfigureAwait(false);

        return belief with
        {
            Status = BeliefStatus.Challenged,
            EvidenceAgainstRefs = against,
            LastEvaluatedAtUtc = Iso(_clock.UtcNow),
        };
    }

    public async Task<Belief> NarrowAsync(
        string beliefId, string scopeJson, string reason, CancellationToken ct)
    {
        Belief belief = await RequireAsync(beliefId, ct).ConfigureAwait(false);

        if (belief.Status != BeliefStatus.Challenged)
        {
            throw new BeliefException(
                $"Narrowing answers a contradiction; this belief is {belief.Status}.");
        }

        if (string.IsNullOrWhiteSpace(scopeJson) || scopeJson == belief.ScopeJson)
        {
            // Reactivating without narrowing would be answering a contradiction by ignoring it.
            throw new BeliefException(
                "Narrowing means a smaller scope than before; this one is unchanged.");
        }

        DateTimeOffset now = _clock.UtcNow;

        await ExecuteAsync("""
            UPDATE belief
               SET status = @active, scope_json = @scope, valid_from_utc = @from,
                   last_evaluated_at_utc = @from, review_at_utc = @review
             WHERE id = @id;
            """, ct,
            ("@active", BeliefStatus.Active), ("@scope", scopeJson), ("@from", Iso(now)),
            ("@review", Iso(now + _policy.ReviewAfter)), ("@id", beliefId)).ConfigureAwait(false);

        await RecordUpdateAsync(beliefId, "scope", 0, $"narrowed: {reason}", ct).ConfigureAwait(false);

        return belief with
        {
            Status = BeliefStatus.Active,
            ScopeJson = scopeJson,
            ValidFromUtc = Iso(now),
            LastEvaluatedAtUtc = Iso(now),
        };
    }

    public async Task<Belief> RetractAsync(string beliefId, string reason, CancellationToken ct)
    {
        Belief belief = await RequireAsync(beliefId, ct).ConfigureAwait(false);

        await ExecuteAsync(
            "UPDATE belief SET status = @retracted, last_evaluated_at_utc = @at WHERE id = @id;", ct,
            ("@retracted", BeliefStatus.Retracted), ("@at", Iso(_clock.UtcNow)), ("@id", beliefId))
            .ConfigureAwait(false);

        await RecordUpdateAsync(beliefId, "retraction", 0, reason, ct).ConfigureAwait(false);

        return belief with { Status = BeliefStatus.Retracted };
    }

    /// <summary>
    /// Ages beliefs nobody has confirmed, and expires those past review (rule 3).
    /// </summary>
    /// <remarks>
    /// A belief that never weakened would be a fact, which is the confusion this whole system
    /// exists to prevent. User-stated beliefs decay too — rule 4 says they may prevail, not that
    /// they stop needing to be true.
    /// </remarks>
    public async Task<int> ReviewDueAsync(CancellationToken ct)
    {
        DateTimeOffset now = _clock.UtcNow;

        IReadOnlyList<Belief> live = await ReadAsync("""
            SELECT id, subject_ref, predicate, object_json, scope_json, confidence,
                   evidence_for_refs, evidence_against_refs, basis, status, valid_from_utc,
                   review_at_utc, last_evaluated_at_utc, decision_impact
              FROM belief WHERE status IN (@active, @candidate);
            """, ct,
            ("@active", BeliefStatus.Active), ("@candidate", BeliefStatus.Candidate))
            .ConfigureAwait(false);

        var touched = 0;

        foreach (Belief belief in live)
        {
            var elapsed = (now - Parse(belief.LastEvaluatedAtUtc)).TotalHours;
            if (elapsed <= 0)
            {
                continue;
            }

            var confidence = belief.Confidence
                * Math.Pow(0.5, elapsed / _policy.ConfidenceHalfLife.TotalHours);

            var expired = Parse(belief.ReviewAtUtc) <= now;

            var status = expired
                ? BeliefStatus.Expired
                : confidence < _policy.CandidateThreshold && belief.Status == BeliefStatus.Active
                    ? BeliefStatus.Candidate
                    : belief.Status;

            if (Math.Abs(confidence - belief.Confidence) < 0.0001 && status == belief.Status)
            {
                continue;
            }

            await ExecuteAsync(
                "UPDATE belief SET confidence = @c, status = @s WHERE id = @id;", ct,
                ("@c", confidence), ("@s", status), ("@id", belief.Id)).ConfigureAwait(false);

            touched++;
        }

        return touched;
    }

    public async Task<Belief?> GetAsync(string beliefId, CancellationToken ct)
    {
        IReadOnlyList<Belief> found = await ReadAsync("""
            SELECT id, subject_ref, predicate, object_json, scope_json, confidence,
                   evidence_for_refs, evidence_against_refs, basis, status, valid_from_utc,
                   review_at_utc, last_evaluated_at_utc, decision_impact
              FROM belief WHERE id = @id;
            """, ct, ("@id", beliefId)).ConfigureAwait(false);

        return found.Count == 0 ? null : found[0];
    }

    public async Task<IReadOnlyList<BeliefUpdate>> UpdatesAsync(string beliefId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, belief_id, observation_ref, delta_confidence, reason, applied_at_utc
              FROM belief_update WHERE belief_id = @id ORDER BY applied_at_utc;
            """;
        command.Parameters.AddWithValue("@id", beliefId);

        var updates = new List<BeliefUpdate>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            updates.Add(new BeliefUpdate(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetDouble(3), reader.GetString(4), reader.GetString(5)));
        }

        return updates;
    }

    // ---- plumbing ----

    private static bool IsModelOutput(string reference) =>
        ModelPrefixes.Any(prefix => reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private Task RecordUpdateAsync(
        string beliefId, string observationRef, double delta, string reason, CancellationToken ct) =>
        ExecuteAsync("""
            INSERT INTO belief_update
                (id, belief_id, observation_ref, delta_confidence, reason, applied_at_utc)
            VALUES (@id, @belief, @obs, @delta, @reason, @at);
            """, ct,
            ("@id", Guid.NewGuid().ToString("N")), ("@belief", beliefId), ("@obs", observationRef),
            ("@delta", delta), ("@reason", reason), ("@at", Iso(_clock.UtcNow)));

    private async Task<Belief> RequireAsync(string beliefId, CancellationToken ct) =>
        await GetAsync(beliefId, ct).ConfigureAwait(false)
        ?? throw new BeliefException("Unknown belief.");

    private async Task<IReadOnlyList<Belief>> ReadAsync(
        string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((var name, var value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var beliefs = new List<Belief>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            beliefs.Add(new Belief(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetDouble(5),
                Lines(reader.GetString(6)), Lines(reader.GetString(7)),
                reader.GetString(8), reader.GetString(9), reader.GetString(10),
                reader.GetString(11), reader.GetString(12), reader.GetString(13)));
        }

        return beliefs;
    }

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

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
