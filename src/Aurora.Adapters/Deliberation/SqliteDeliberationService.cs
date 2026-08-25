using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Vault;
using Aurora.Core;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Deliberation;

/// <summary>
/// Internal deliberation and explainable synthesis (RFC 025).
/// </summary>
/// <remarks>
/// The class is defined by a separation: how Aurora worked, and what it can say about it. The first
/// is encrypted, short-lived and unreadable through any method here; the second is a
/// <see cref="Thought"/> — reason, sources, next effect — and is the only thing anyone gets when
/// they ask why.
/// <para>
/// That is not only a privacy argument. A transcript of intermediate reasoning is not an
/// explanation, and treating one as the other designs the product around a non-deterministic
/// process instead of around what can actually be checked.
/// </para>
/// </remarks>
public sealed class SqliteDeliberationService : IDeliberationService
{
    /// <summary>How long protected technical material is kept.</summary>
    /// <remarks>
    /// Rule 4 says minimise and limit. Long enough to diagnose a decision that went wrong while
    /// somebody still cares, short enough that it is not a growing record of everything Aurora has
    /// ever half-thought.
    /// </remarks>
    private static readonly TimeSpan TraceRetention = TimeSpan.FromDays(7);

    private readonly SqliteConnectionFactory _factory;
    private readonly ICognitiveCycle _cycles;
    private readonly AesGcmSecretProtector _protector;
    private readonly IClock _clock;

    public SqliteDeliberationService(
        SqliteConnectionFactory factory,
        ICognitiveCycle cycles,
        AesGcmSecretProtector protector,
        IClock clock)
    {
        _factory = factory;
        _cycles = cycles;
        _protector = protector;
        _clock = clock;
    }

    public async Task<DeliberationState> StartAsync(
        string cycleId, string question, DateTimeOffset deadline, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new DeliberationException("A deliberation is about a question; give it one.");
        }

        // Rule 1: it belongs to a cycle, and the cycle has to exist. A deliberation attached to
        // nothing is the ownerless global mental process the rule forbids.
        CognitiveCycle cycle = await _cycles.GetAsync(cycleId, ct).ConfigureAwait(false)
            ?? throw new DeliberationException("Unknown cycle; a deliberation belongs to one.");

        if (cycle.Status is CycleStatus.Completed or CycleStatus.Failed or CycleStatus.Cancelled)
        {
            throw new DeliberationException($"That cycle is {cycle.Status}; it deliberates no further.");
        }

        if (deadline <= _clock.UtcNow)
        {
            // The other half of rule 1. Something that never has to finish is not bounded by a
            // cycle in any sense that matters.
            throw new DeliberationException("A deliberation needs a deadline in the future.");
        }

        var state = new DeliberationState(
            Guid.NewGuid().ToString("N"), cycleId, DeliberationPhase.Orient, question,
            UnresolvedQuestions: [question], CandidateRefs: [], Assertions: [], Uncertainty: [],
            NextStep: null, DeliberationStatus.Active, TraceRef: null,
            Iso(_clock.UtcNow + TraceRetention), Iso(_clock.UtcNow), Iso(deadline));

        await ExecuteAsync("""
            INSERT INTO deliberation
                (id, cycle_id, phase, active_question, unresolved_questions, candidate_refs,
                 assertions, uncertainty, next_step, status, trace_ref, retention_until_utc,
                 started_at_utc, deadline_at_utc)
            VALUES (@id, @cycle, @phase, @q, @unresolved, '', '[]', '', NULL, @status, NULL,
                    @retention, @started, @deadline);
            """, ct,
            ("@id", state.Id), ("@cycle", cycleId), ("@phase", state.Phase), ("@q", question),
            ("@unresolved", question), ("@status", state.Status),
            ("@retention", state.RetentionUntilUtc), ("@started", state.StartedAtUtc),
            ("@deadline", state.DeadlineAtUtc)).ConfigureAwait(false);

        return state;
    }

    public async Task<DeliberationState> AdvanceAsync(
        string deliberationId, string phase, DeliberationStep step, CancellationToken ct)
    {
        DeliberationState state = await RequireAsync(deliberationId, ct).ConfigureAwait(false);

        if (state.Status != DeliberationStatus.Active)
        {
            throw new DeliberationException($"This deliberation is {state.Status}.");
        }

        if (!DeliberationPhase.IsKnown(phase))
        {
            throw new DeliberationException($"Unknown phase '{phase}'.");
        }

        // Phases move forward. Deliberation that can revisit any phase at will has no shape, and a
        // record of it explains nothing about the order things were considered in.
        if (DeliberationPhase.IndexOf(phase) < DeliberationPhase.IndexOf(state.Phase))
        {
            throw new DeliberationException(
                $"{phase} comes before {state.Phase}; a deliberation does not go back.");
        }

        if (Parse(state.DeadlineAtUtc) <= _clock.UtcNow)
        {
            throw new DeliberationException("The deadline passed; close it rather than continuing.");
        }

        var assertions = state.Assertions.Concat(step.Assertions ?? []).ToList();
        var candidates = state.CandidateRefs.Concat(step.CandidateRefs ?? [])
            .Distinct(StringComparer.Ordinal).ToList();
        var uncertainty = state.Uncertainty.Concat(step.Uncertainty ?? [])
            .Distinct(StringComparer.Ordinal).ToList();

        var unresolved = state.UnresolvedQuestions
            .Except(step.ResolvedQuestions ?? [], StringComparer.Ordinal)
            .Concat(step.NewQuestions ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var traceRef = state.TraceRef;
        if (!string.IsNullOrWhiteSpace(step.Trace))
        {
            traceRef = await WriteTraceAsync(deliberationId, state.TraceRef, step.Trace, ct)
                .ConfigureAwait(false);
        }

        await ExecuteAsync("""
            UPDATE deliberation
               SET phase = @phase, unresolved_questions = @unresolved, candidate_refs = @candidates,
                   assertions = @assertions, uncertainty = @uncertainty, next_step = @next,
                   trace_ref = @trace
             WHERE id = @id;
            """, ct,
            ("@phase", phase), ("@unresolved", string.Join('\n', unresolved)),
            ("@candidates", string.Join('\n', candidates)),
            ("@assertions", AuroraJson.Serialize(assertions)),
            ("@uncertainty", string.Join('\n', uncertainty)),
            ("@next", (object?)(step.NextStep ?? state.NextStep) ?? DBNull.Value),
            ("@trace", (object?)traceRef ?? DBNull.Value),
            ("@id", deliberationId)).ConfigureAwait(false);

        return state with
        {
            Phase = phase,
            UnresolvedQuestions = unresolved,
            CandidateRefs = candidates,
            Assertions = assertions,
            Uncertainty = uncertainty,
            NextStep = step.NextStep ?? state.NextStep,
            TraceRef = traceRef,
        };
    }

    public async Task<Thought> SummariseAsync(
        string deliberationId, ThoughtRequest request, CancellationToken ct)
    {
        DeliberationState state = await RequireAsync(deliberationId, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(request.RecommendedOption))
        {
            throw new DeliberationException("A summary recommends something, even if that is to ask.");
        }

        // Built from the state, never from the trace. The two are read from different tables by
        // different code paths on purpose: it is not possible to accidentally summarise the
        // protected material, because nothing here reads it.
        var evidence = state.Assertions
            .SelectMany(a => a.EvidenceRefs)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var uncertainty = state.Uncertainty
            .Concat(state.Assertions.Where(a => a.IsHypothesis).Select(a => $"unsupported: {a.Claim}"))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var thought = new Thought(
            Guid.NewGuid().ToString("N"), state.CycleId, deliberationId, request.Intent,
            request.ObjectiveRef, evidence, request.Assumptions ?? [], request.Options,
            uncertainty, request.RecommendedOption,
            Explain(state, request, evidence, uncertainty),
            ThoughtStatus.Draft, Iso(_clock.UtcNow));

        await ExecuteAsync("""
            INSERT INTO thought
                (id, cycle_id, deliberation_id, intent, objective_ref, evidence_refs, assumptions,
                 options, uncertainty, recommended_option, user_explanation, status, created_at_utc)
            VALUES (@id, @cycle, @delib, @intent, @objective, @evidence, @assumptions, @options,
                    @uncertainty, @recommended, @explanation, @status, @at);
            """, ct,
            ("@id", thought.Id), ("@cycle", thought.CycleId), ("@delib", deliberationId),
            ("@intent", thought.Intent),
            ("@objective", (object?)thought.ObjectiveRef ?? DBNull.Value),
            ("@evidence", string.Join('\n', thought.EvidenceRefs)),
            ("@assumptions", string.Join('\n', thought.Assumptions)),
            ("@options", string.Join('\n', thought.Options)),
            ("@uncertainty", string.Join('\n', thought.Uncertainty)),
            ("@recommended", thought.RecommendedOption),
            ("@explanation", thought.UserExplanation), ("@status", thought.Status),
            ("@at", thought.CreatedAtUtc)).ConfigureAwait(false);

        return thought;
    }

    /// <summary>
    /// Composes the user-facing explanation from three stated parts.
    /// </summary>
    /// <remarks>
    /// Reason, sources, next effect — and nothing else, because rule 3 forbids offering "I am
    /// thinking" as evidence of work and a free-form field is exactly where that sentence gets in.
    /// The shape here cannot express a claim about ongoing internal activity: there is no clause
    /// for one.
    /// </remarks>
    private static string Explain(
        DeliberationState state, ThoughtRequest request,
        IReadOnlyList<string> evidence, IReadOnlyList<string> uncertainty)
    {
        var sources = evidence.Count == 0
            ? "no source supports this yet"
            : $"{evidence.Count} source(s): {string.Join(", ", evidence.Take(5))}";

        var caveat = uncertainty.Count == 0
            ? string.Empty
            : $" Unresolved: {string.Join("; ", uncertainty.Take(3))}.";

        return $"Because: {state.ActiveQuestion} Sources: {sources}. "
             + $"Next: {request.RecommendedOption}.{caveat}";
    }

    public async Task<DeliberationState> CloseAsync(
        string deliberationId, string disposition, CancellationToken ct)
    {
        DeliberationState state = await RequireAsync(deliberationId, ct).ConfigureAwait(false);

        if (!DeliberationDisposition.IsKnown(disposition))
        {
            throw new DeliberationException($"'{disposition}' is not a way for a deliberation to end.");
        }

        // Limit case: an inconclusive deliberation produces ASK or WAIT with concrete questions.
        // Closing one with nothing to ask would be reporting a dead end as a conclusion.
        if (disposition == DeliberationDisposition.Inconclusive && state.UnresolvedQuestions.Count == 0)
        {
            throw new DeliberationException(
                "An inconclusive deliberation leaves the questions it could not answer; there are none.");
        }

        if (disposition == DeliberationDisposition.Concluded && state.UnresolvedQuestions.Count > 0)
        {
            throw new DeliberationException(
                $"{state.UnresolvedQuestions.Count} question(s) are still open; this is INCONCLUSIVE.");
        }

        await ExecuteAsync(
            "UPDATE deliberation SET status = @status, next_step = @next WHERE id = @id;", ct,
            ("@status", DeliberationStatus.Closed), ("@next", disposition), ("@id", deliberationId))
            .ConfigureAwait(false);

        return state with { Status = DeliberationStatus.Closed, NextStep = disposition };
    }

    public async Task<bool> TraceAvailableAsync(string deliberationId, CancellationToken ct)
    {
        DeliberationState state = await RequireAsync(deliberationId, ct).ConfigureAwait(false);
        if (state.TraceRef is null)
        {
            return false;
        }

        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM deliberation_trace WHERE trace_ref = @ref AND retention_until_utc > @now;";
        command.Parameters.AddWithValue("@ref", state.TraceRef);
        command.Parameters.AddWithValue("@now", Iso(_clock.UtcNow));

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
    }

    public async Task<int> ExpireDueAsync(CancellationToken ct)
    {
        // The material goes, permanently. What survives is the Thought, which is the part that was
        // ever meant to outlive the working.
        var discarded = await ExecuteAsync(
            "DELETE FROM deliberation_trace WHERE retention_until_utc <= @now;", ct,
            ("@now", Iso(_clock.UtcNow))).ConfigureAwait(false);

        var closed = await ExecuteAsync("""
            UPDATE deliberation
               SET status = @closed, next_step = @expired
             WHERE status = @active AND deadline_at_utc <= @now;
            """, ct,
            ("@closed", DeliberationStatus.Closed), ("@expired", DeliberationDisposition.Expired),
            ("@active", DeliberationStatus.Active), ("@now", Iso(_clock.UtcNow))).ConfigureAwait(false);

        return discarded + closed;
    }

    public async Task<DeliberationState?> GetAsync(string deliberationId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, cycle_id, phase, active_question, unresolved_questions, candidate_refs,
                   assertions, uncertainty, next_step, status, trace_ref, retention_until_utc,
                   started_at_utc, deadline_at_utc
              FROM deliberation WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@id", deliberationId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new DeliberationState(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            Lines(reader.GetString(4)), Lines(reader.GetString(5)),
            AuroraJson.Deserialize<List<Assertion>>(reader.GetString(6)),
            Lines(reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetString(11), reader.GetString(12), reader.GetString(13));
    }

    public async Task<Thought?> ThoughtAsync(string thoughtId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, cycle_id, deliberation_id, intent, objective_ref, evidence_refs, assumptions,
                   options, uncertainty, recommended_option, user_explanation, status, created_at_utc
              FROM thought WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@id", thoughtId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new Thought(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            Lines(reader.GetString(5)), Lines(reader.GetString(6)), Lines(reader.GetString(7)),
            Lines(reader.GetString(8)), reader.GetString(9), reader.GetString(10),
            reader.GetString(11), reader.GetString(12));
    }

    public async Task<IReadOnlyList<Thought>> ThoughtsForCycleAsync(string cycleId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, cycle_id, deliberation_id, intent, objective_ref, evidence_refs, assumptions,
                   options, uncertainty, recommended_option, user_explanation, status, created_at_utc
              FROM thought WHERE cycle_id = @cycle ORDER BY created_at_utc;
            """;
        command.Parameters.AddWithValue("@cycle", cycleId);

        var thoughts = new List<Thought>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            thoughts.Add(new Thought(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                Lines(reader.GetString(5)), Lines(reader.GetString(6)), Lines(reader.GetString(7)),
                Lines(reader.GetString(8)), reader.GetString(9), reader.GetString(10),
                reader.GetString(11), reader.GetString(12)));
        }

        return thoughts;
    }

    // ---- the protected half ----

    /// <summary>
    /// Writes working notes, encrypted, replacing whatever was there.
    /// </summary>
    /// <remarks>
    /// Replaced rather than appended: rule 4 says minimise, and an append-only trace is the
    /// opposite of minimising. The deliberation's own id is the associated data, so a trace cannot
    /// be moved from one deliberation to another and still decrypt.
    /// </remarks>
    private async Task<string> WriteTraceAsync(
        string deliberationId, string? existingRef, string trace, CancellationToken ct)
    {
        var traceRef = existingRef ?? $"trace/{Guid.NewGuid():N}";
        SealedSecret sealed_ = _protector.Protect(deliberationId, trace);

        await ExecuteAsync("""
            INSERT INTO deliberation_trace
                (trace_ref, deliberation_id, nonce, ciphertext, tag, written_at_utc, retention_until_utc)
            VALUES (@ref, @id, @nonce, @cipher, @tag, @at, @retention)
            ON CONFLICT(trace_ref) DO UPDATE SET
                nonce = @nonce, ciphertext = @cipher, tag = @tag, written_at_utc = @at;
            """, ct,
            ("@ref", traceRef), ("@id", deliberationId), ("@nonce", sealed_.Nonce),
            ("@cipher", sealed_.Ciphertext), ("@tag", sealed_.Tag),
            ("@at", Iso(_clock.UtcNow)),
            ("@retention", Iso(_clock.UtcNow + TraceRetention))).ConfigureAwait(false);

        return traceRef;
    }

    private async Task<DeliberationState> RequireAsync(string deliberationId, CancellationToken ct) =>
        await GetAsync(deliberationId, ct).ConfigureAwait(false)
        ?? throw new DeliberationException("Unknown deliberation.");

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
