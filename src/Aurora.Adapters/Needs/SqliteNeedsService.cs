using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Needs;

/// <summary>
/// Operational needs (RFC 031): what is waiting on Aurora, and in what order.
/// </summary>
/// <remarks>
/// Needs are what limited autonomy is steered by. They are not desires and they authorise nothing:
/// the strongest thing a need can do on its own is draft a goal, and running that goal goes through
/// the cycle, the policies and the approvals like any other work.
/// </remarks>
public sealed class SqliteNeedsService : INeedsService
{
    /// <summary>How long an unattended need takes to lose half its intensity.</summary>
    /// <remarks>
    /// Rule 4 forbids eternal urgencies. Something nobody acted on for a day is, by observation,
    /// less urgent than it claimed to be — so it gets quieter rather than louder, and the loud ones
    /// stay meaningful.
    /// </remarks>
    private static readonly TimeSpan HalfLife = TimeSpan.FromHours(24);

    private readonly SqliteConnectionFactory _factory;
    private readonly IPlanner _planner;
    private readonly IClock _clock;

    public SqliteNeedsService(SqliteConnectionFactory factory, IPlanner planner, IClock clock)
    {
        _factory = factory;
        _planner = planner;
        _clock = clock;
    }

    public async Task<IReadOnlyList<Need>> DetectAsync(
        NeedsSnapshot snapshot, IReadOnlyList<Signal> signals, CancellationToken ct)
    {
        var detected = new List<Need>();

        foreach (NeedCandidate candidate in Candidates(snapshot, signals))
        {
            detected.Add(await UpsertAsync(candidate, ct).ConfigureAwait(false));
        }

        return detected;
    }

    /// <summary>
    /// The conditions that become needs, each with what proves it and what would end it.
    /// </summary>
    /// <remarks>
    /// Every entry is derived from something counted or measured. Rule 1 requires evidence and a
    /// measurable satisfaction condition, so both are written next to the rule that produces them
    /// rather than filled in afterwards — a need with no stated end never stops being urgent.
    /// </remarks>
    private static IEnumerable<NeedCandidate> Candidates(
        NeedsSnapshot snapshot, IReadOnlyList<Signal> signals)
    {
        if (snapshot.UnreconciledReservations is > 0)
        {
            yield return new NeedCandidate(
                NeedKind.Recovery, "kernel/idempotency",
                $"{snapshot.UnreconciledReservations} reservation(s) ended in an indeterminate state",
                "no reservation is left in UNKNOWN",
                Intensity: Scale(snapshot.UnreconciledReservations.Value, 3), Priority: 1, NeedOwner.System);
        }

        if (snapshot.SinceLastBackup is { } age && age > TimeSpan.FromDays(1))
        {
            yield return new NeedCandidate(
                NeedKind.Safety, "mind-state/backup",
                $"the last backup is {age.TotalDays:F0} day(s) old",
                "a backup completed within the last day",
                Intensity: Scale(age.TotalDays, 7), Priority: 1, NeedOwner.System);
        }

        if (snapshot.PendingApprovals is > 0)
        {
            // Owned by the person, not the system: this is Aurora waiting on them, and it belongs
            // above maintenance for exactly that reason.
            yield return new NeedCandidate(
                NeedKind.Communication, "approvals/pending",
                $"{snapshot.PendingApprovals} approval(s) are waiting on a person",
                "no approval is pending",
                Intensity: Scale(snapshot.PendingApprovals.Value, 5), Priority: 2, NeedOwner.User);
        }

        if (snapshot.OverdueGoals is > 0)
        {
            yield return new NeedCandidate(
                NeedKind.Obligation, "goals/overdue",
                $"{snapshot.OverdueGoals} goal(s) are past their deadline",
                "no goal is past its deadline",
                Intensity: Scale(snapshot.OverdueGoals.Value, 5), Priority: 2, NeedOwner.User);
        }

        if (snapshot.DeadLetters is > 0)
        {
            yield return new NeedCandidate(
                NeedKind.Maintenance, "events/dead-letters",
                $"{snapshot.DeadLetters} delivery(ies) are in the dead-letter queue",
                "the dead-letter queue is empty",
                Intensity: Scale(snapshot.DeadLetters.Value, 10), Priority: 3, NeedOwner.System);
        }

        if (snapshot.MissedScheduleRuns is > 0)
        {
            yield return new NeedCandidate(
                NeedKind.Maintenance, "schedules/missed",
                $"{snapshot.MissedScheduleRuns} scheduled occurrence(s) did not run",
                "no schedule has an unreviewed missed run",
                Intensity: Scale(snapshot.MissedScheduleRuns.Value, 10), Priority: 3, NeedOwner.System);
        }

        if (snapshot.UnconsolidatedMemories is > 0)
        {
            yield return new NeedCandidate(
                NeedKind.Consolidation, "memory/unconsolidated",
                $"{snapshot.UnconsolidatedMemories} memory(ies) are awaiting consolidation",
                "nothing is left awaiting consolidation",
                Intensity: Scale(snapshot.UnconsolidatedMemories.Value, 50), Priority: 4, NeedOwner.System);
        }

        // A signal severe enough to matter becomes a need, so that what deserved attention still
        // deserves it once the signal itself has expired.
        foreach (Signal signal in signals.Where(s => SignalSeverity.Rank(s.Severity) >= SignalSeverity.Rank(SignalSeverity.High)))
        {
            yield return new NeedCandidate(
                NeedKind.Safety, $"signal/{signal.Id}",
                $"{signal.Severity} {signal.Kind} signal from {signal.SourceEventRef}",
                $"signal {signal.Id} is resolved",
                Intensity: Math.Max(signal.Urgency, 0.7), Priority: 1, NeedOwner.System,
                EvidenceRef: signal.Id);
        }
    }

    public async Task<IReadOnlyList<Need>> RankAsync(CancellationToken ct)
    {
        // Deferred needs are included: putting one aside sets the time it may be looked at again,
        // and the clause below is what enforces that. Excluding the status as well would mean a
        // deferred need never came back at all, which is not deferral, it is dropping it.
        IReadOnlyList<Need> open = await ReadAsync($"""
            {Select}
             WHERE status IN (@detected, @acknowledged, @planned, @deferred)
               AND (earliest_action_at_utc IS NULL OR earliest_action_at_utc <= @now)
            """, ct,
            ("@detected", NeedStatus.Detected), ("@acknowledged", NeedStatus.Acknowledged),
            ("@planned", NeedStatus.Planned), ("@deferred", NeedStatus.Deferred),
            ("@now", Iso(_clock.UtcNow))).ConfigureAwait(false);

        // Rule 3: safety and recovery first, because those are the system failing to keep its own
        // promises. Then what the person asked for. Then maintenance, which is the work that can
        // always wait one more hour and must therefore never be allowed to push in front.
        return open
            .OrderBy(n => NeedKind.IsIncident(n.Kind) ? 0 : n.Owner == NeedOwner.User ? 1 : 2)
            .ThenBy(n => n.Priority)
            .ThenByDescending(n => n.Intensity)
            .ThenBy(n => n.DetectedAtUtc, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<Need> PlanAsync(string needId, CancellationToken ct)
    {
        Need need = await RequireAsync(needId, ct).ConfigureAwait(false);

        if (need.Status is NeedStatus.Satisfied or NeedStatus.Expired)
        {
            throw new NeedException($"A {need.Status} need is not planned; detect a new one.");
        }

        if (need.RecommendedGoalRef is not null)
        {
            return need;
        }

        // Rule 2: DRAFT and nothing further. An internal need is a reason to consider something,
        // not a decision to do it, and the difference is the whole of limited autonomy.
        Goal goal = await _planner.DraftAsync(
            new GoalRequest(
                Title: $"{need.Kind}: {need.SubjectRef}",
                Outcome: need.SatisfactionCondition,
                OwnerId: need.Owner,
                SuccessCriteria: [need.SatisfactionCondition],
                Assumptions: ["raised by Aurora from an observed condition, not requested by the owner"],
                Priority: need.Priority,
                ApprovalPolicyId: null),
            ct).ConfigureAwait(false);

        await ExecuteAsync(
            "UPDATE need SET status = @s, recommended_goal_ref = @g WHERE id = @id;", ct,
            ("@s", NeedStatus.Planned), ("@g", goal.Id), ("@id", needId)).ConfigureAwait(false);

        return need with { Status = NeedStatus.Planned, RecommendedGoalRef = goal.Id };
    }

    public async Task<Need> SatisfyAsync(string needId, string evidenceRef, CancellationToken ct)
    {
        Need need = await RequireAsync(needId, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(evidenceRef))
        {
            // Rule 1 again, from the other end: a need is met against something, or it is simply
            // being declared met, which is how maintenance quietly stops happening.
            throw new NeedException(
                $"Satisfying a need needs the evidence for it: {need.SatisfactionCondition}.");
        }

        await ExecuteAsync(
            "UPDATE need SET status = @s, satisfied_evidence_ref = @e, intensity = 0 WHERE id = @id;", ct,
            ("@s", NeedStatus.Satisfied), ("@e", evidenceRef), ("@id", needId)).ConfigureAwait(false);

        return need with { Status = NeedStatus.Satisfied, SatisfiedEvidenceRef = evidenceRef, Intensity = 0 };
    }

    public async Task<Need> DeferAsync(
        string needId, DateTimeOffset until, string reason, CancellationToken ct)
    {
        Need need = await RequireAsync(needId, ct).ConfigureAwait(false);

        if (NeedKind.IsIncident(need.Kind))
        {
            throw new NeedException($"A {need.Kind} need is not deferred; it is resolved or it stands.");
        }

        await ExecuteAsync("""
            UPDATE need
               SET status = @s, earliest_action_at_utc = @until,
                   policy_constraints = @reason
             WHERE id = @id;
            """, ct,
            ("@s", NeedStatus.Deferred), ("@until", Iso(until)), ("@reason", reason), ("@id", needId))
            .ConfigureAwait(false);

        return need with
        {
            Status = NeedStatus.Deferred,
            EarliestActionAtUtc = Iso(until),
            PolicyConstraints = [reason],
        };
    }

    public async Task<int> DecayAsync(CancellationToken ct)
    {
        var expired = await ExecuteAsync("""
            UPDATE need
               SET status = @expired, intensity = 0
             WHERE expires_at_utc IS NOT NULL AND expires_at_utc <= @now
               AND status NOT IN (@satisfied, @expired);
            """, ct,
            ("@expired", NeedStatus.Expired), ("@now", Iso(_clock.UtcNow)),
            ("@satisfied", NeedStatus.Satisfied)).ConfigureAwait(false);

        IReadOnlyList<Need> open = await ReadAsync($"""
            {Select} WHERE status IN (@detected, @acknowledged, @deferred)
            """, ct,
            ("@detected", NeedStatus.Detected), ("@acknowledged", NeedStatus.Acknowledged),
            ("@deferred", NeedStatus.Deferred)).ConfigureAwait(false);

        DateTimeOffset now = _clock.UtcNow;
        var decayed = 0;

        foreach (Need need in open)
        {
            var elapsed = (now - Parse(need.DetectedAtUtc)).TotalHours;
            if (elapsed <= 0)
            {
                continue;
            }

            var intensity = need.Intensity * Math.Pow(0.5, elapsed / HalfLife.TotalHours);
            if (Math.Abs(intensity - need.Intensity) < 0.0001)
            {
                continue;
            }

            await ExecuteAsync(
                "UPDATE need SET intensity = @i WHERE id = @id;", ct,
                ("@i", intensity), ("@id", need.Id)).ConfigureAwait(false);

            decayed++;
        }

        return expired + decayed;
    }

    public async Task<Need?> GetAsync(string needId, CancellationToken ct)
    {
        IReadOnlyList<Need> found = await ReadAsync(
            $"{Select} WHERE id = @id;", ct, ("@id", needId)).ConfigureAwait(false);

        return found.Count == 0 ? null : found[0];
    }

    // ---- plumbing ----

    private sealed record NeedCandidate(
        string Kind, string SubjectRef, string Evidence, string SatisfactionCondition,
        double Intensity, int Priority, string Owner, string? EvidenceRef = null);

    /// <summary>
    /// Records a condition, refreshing the open need for that subject rather than adding another.
    /// </summary>
    /// <remarks>
    /// The list of needs is a description of how things stand, not a history of every time Aurora
    /// noticed. Twenty ticks with the same dead-letter queue is one need, not twenty.
    /// </remarks>
    private async Task<Need> UpsertAsync(NeedCandidate candidate, CancellationToken ct)
    {
        IReadOnlyList<Need> existing = await ReadAsync($"""
            {Select}
             WHERE subject_ref = @s AND status NOT IN (@satisfied, @expired)
             ORDER BY detected_at_utc DESC
            """, ct,
            ("@s", candidate.SubjectRef), ("@satisfied", NeedStatus.Satisfied),
            ("@expired", NeedStatus.Expired)).ConfigureAwait(false);

        var intensity = Math.Clamp(candidate.Intensity, 0, 1);

        if (existing.Count > 0)
        {
            Need current = existing[0];
            await ExecuteAsync(
                "UPDATE need SET intensity = @i, evidence_refs = @e WHERE id = @id;", ct,
                ("@i", intensity), ("@e", candidate.Evidence), ("@id", current.Id)).ConfigureAwait(false);

            return current with { Intensity = intensity, EvidenceRefs = [candidate.Evidence] };
        }

        var need = new Need(
            Guid.NewGuid().ToString("N"), candidate.Kind, candidate.SubjectRef, intensity,
            candidate.Priority, [candidate.EvidenceRef ?? candidate.Evidence],
            candidate.SatisfactionCondition, EarliestActionAtUtc: null, ExpiresAtUtc: null,
            RecommendedGoalRef: null, NeedStatus.Detected, PolicyConstraints: [],
            candidate.Owner, Iso(_clock.UtcNow));

        await ExecuteAsync("""
            INSERT INTO need
                (id, kind, subject_ref, intensity, priority, evidence_refs, satisfaction_condition,
                 earliest_action_at_utc, expires_at_utc, recommended_goal_ref, status,
                 policy_constraints, owner, detected_at_utc, satisfied_evidence_ref)
            VALUES (@id, @kind, @subject, @i, @p, @evidence, @condition, NULL, NULL, NULL,
                    @status, '', @owner, @at, NULL);
            """, ct,
            ("@id", need.Id), ("@kind", need.Kind), ("@subject", need.SubjectRef),
            ("@i", need.Intensity), ("@p", need.Priority),
            ("@evidence", string.Join('\n', need.EvidenceRefs)),
            ("@condition", need.SatisfactionCondition), ("@status", need.Status),
            ("@owner", need.Owner), ("@at", need.DetectedAtUtc)).ConfigureAwait(false);

        return need;
    }

    /// <summary>Scales a count into 0..1, saturating at the point where more stops meaning worse.</summary>
    private static double Scale(double observed, double saturation) =>
        Math.Clamp(observed / saturation, 0.1, 1.0);

    private async Task<Need> RequireAsync(string needId, CancellationToken ct) =>
        await GetAsync(needId, ct).ConfigureAwait(false)
        ?? throw new NeedException("Unknown need.");

    private const string Select = """
        SELECT id, kind, subject_ref, intensity, priority, evidence_refs, satisfaction_condition,
               earliest_action_at_utc, expires_at_utc, recommended_goal_ref, status,
               policy_constraints, owner, detected_at_utc, satisfied_evidence_ref
          FROM need
        """;

    private async Task<IReadOnlyList<Need>> ReadAsync(
        string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((var name, var value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var needs = new List<Need>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            needs.Add(new Need(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3),
                reader.GetInt32(4), Lines(reader.GetString(5)), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetString(10), Lines(reader.GetString(11)), reader.GetString(12),
                reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14)));
        }

        return needs;
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

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
