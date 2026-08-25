using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Signals;

/// <summary>
/// The Signal System (RFC 030): what deserves attention, and in what order.
/// </summary>
/// <remarks>
/// Nothing here can act. Routing changes where Aurora looks and how soon; a CRITICAL signal reaches
/// the front of the queue and then waits for exactly the same decision and permission as anything
/// else. Urgency that granted authority would be the shortest path around every check the system
/// has, which is why rule 2 exists and why this class holds no capability.
/// </remarks>
public sealed class SqliteSignalService : ISignalService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ICognitiveCycle _cycles;
    private readonly IClock _clock;

    public SqliteSignalService(
        SqliteConnectionFactory factory, ICognitiveCycle cycles, IClock clock)
    {
        _factory = factory;
        _cycles = cycles;
        _clock = clock;
    }

    public async Task<Signal> EmitAsync(
        string sourceEventRef, SignalClassification classification, SignalPolicy policy, CancellationToken ct)
    {
        if (!SignalKind.IsKnown(classification.Kind) || !SignalSeverity.IsKnown(classification.Severity))
        {
            throw new SignalException("A signal needs a known kind and severity.");
        }

        if (classification.Lifetime <= TimeSpan.Zero)
        {
            // Rule 4: a signal that never expires is a permanent claim on attention, which is the
            // one thing RFC 030 says a signal must not become.
            throw new SignalException("A signal must expire; give it a lifetime.");
        }

        // Rule 1: the source has to exist. Without this, a classifier could invent the urgency and
        // the evidence for it in the same breath, and nothing downstream could tell the difference.
        if (!await SourceExistsAsync(sourceEventRef, ct).ConfigureAwait(false))
        {
            throw new SignalException(
                $"'{sourceEventRef}' is not an observable source; a signal cannot be raised without one.");
        }

        DateTimeOffset now = _clock.UtcNow;
        var dedupeKey = string.Join(
            '|', classification.Kind, sourceEventRef, string.Join(',', classification.TargetRefs));

        var reasons = new List<string>();
        var status = SignalStatus.New;

        // Storm control. A duplicate inside the window, or too many of the same shape, is held back
        // and says so — recorded as SUPPRESSED rather than dropped, because a signal nobody can
        // find is indistinguishable from one that was never raised.
        var recent = await CountRecentAsync(dedupeKey, now - policy.DedupeWindow, ct).ConfigureAwait(false);
        if (recent > 0)
        {
            status = SignalStatus.Suppressed;
            reasons.Add(recent >= policy.MaxPerWindow ? SignalReason.RateLimited : SignalReason.Duplicate);
        }

        var signal = new Signal(
            Guid.NewGuid().ToString("N"), sourceEventRef, classification.Kind, classification.Severity,
            Clamp(classification.Urgency), Clamp(classification.Relevance), Clamp(classification.Confidence),
            classification.TargetRefs, Iso(now), Iso(now + classification.Lifetime),
            Interruptibility.Queue, status, reasons, classification.PolicyRefs ?? []);

        await ExecuteAsync("""
            INSERT INTO signal
                (id, source_event_ref, kind, severity, urgency, relevance, confidence, target_refs,
                 created_at_utc, expires_at_utc, interruptibility, status, reason_codes, policy_refs,
                 dedupe_key, resolution_ref)
            VALUES (@id, @src, @kind, @sev, @urg, @rel, @conf, @targets, @created, @expires,
                    @interrupt, @status, @reasons, @policies, @dedupe, NULL);
            """, ct,
            ("@id", signal.Id), ("@src", sourceEventRef), ("@kind", signal.Kind),
            ("@sev", signal.Severity), ("@urg", signal.Urgency), ("@rel", signal.Relevance),
            ("@conf", signal.Confidence), ("@targets", string.Join(',', signal.TargetRefs)),
            ("@created", signal.CreatedAtUtc), ("@expires", signal.ExpiresAtUtc),
            ("@interrupt", signal.Interruptibility), ("@status", signal.Status),
            ("@reasons", string.Join(',', signal.ReasonCodes)),
            ("@policies", string.Join(',', signal.PolicyRefs)),
            ("@dedupe", dedupeKey)).ConfigureAwait(false);

        return signal;
    }

    public async Task<RouteDecision> RouteAsync(
        string signalId, string? cycleInProgressId, SignalPolicy policy, CancellationToken ct)
    {
        Signal signal = await RequireAsync(signalId, ct).ConfigureAwait(false);

        if (Parse(signal.ExpiresAtUtc) <= _clock.UtcNow)
        {
            await SetStatusAsync(signalId, SignalStatus.Expired, [SignalReason.Expired], null, ct)
                .ConfigureAwait(false);

            return new RouteDecision(signalId, Interruptibility.Queue, [SignalReason.Expired]);
        }

        if (signal.Status == SignalStatus.Suppressed)
        {
            return new RouteDecision(signalId, Interruptibility.Queue, signal.ReasonCodes);
        }

        var severity = SignalSeverity.Rank(signal.Severity);
        var reasons = new List<string>();

        // Rule 3: interrupting is a policy judgement, not a property of the signal. The same alert
        // is worth stopping for on a quiet evening and not worth it mid-incident, so the threshold
        // lives in configuration and the signal only says how bad it is.
        string level;
        if (severity >= SignalSeverity.Rank(policy.EmergencyAtSeverity))
        {
            level = Interruptibility.Emergency;
            reasons.Add(SignalReason.ThresholdMet);
        }
        else if (severity >= SignalSeverity.Rank(policy.InterruptAtSeverity))
        {
            level = Interruptibility.Interrupt;
            reasons.Add(SignalReason.ThresholdMet);
        }
        else if (cycleInProgressId is null)
        {
            level = Interruptibility.FocusWhenIdle;
            reasons.Add(SignalReason.NothingInProgress);
        }
        else
        {
            level = Interruptibility.Queue;
            reasons.Add(SignalReason.BelowInterruptThreshold);
        }

        string? preserved = null;
        if (Interruptibility.Interrupts(level) && cycleInProgressId is not null)
        {
            // Parked, not cancelled. An urgent alert that destroyed whatever Aurora was in the
            // middle of would make the interruption cost more than the thing it interrupted for.
            await _cycles.WaitAsync(
                cycleInProgressId, $"interrupted by {signal.Severity} signal {signalId}", ct)
                .ConfigureAwait(false);

            preserved = cycleInProgressId;
        }

        await SetStatusAsync(
            signalId,
            Interruptibility.Interrupts(level) ? SignalStatus.Focused : SignalStatus.Queued,
            reasons, level, ct).ConfigureAwait(false);

        return new RouteDecision(signalId, level, reasons, preserved);
    }

    public async Task<Signal> AcknowledgeAsync(string signalId, string resolutionRef, CancellationToken ct)
    {
        Signal signal = await RequireAsync(signalId, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(resolutionRef))
        {
            throw new SignalException("Acknowledging a signal needs the reference that resolved it.");
        }

        await ExecuteAsync(
            "UPDATE signal SET status = @s, resolution_ref = @r WHERE id = @id;", ct,
            ("@s", SignalStatus.Resolved), ("@r", resolutionRef), ("@id", signalId)).ConfigureAwait(false);

        return signal with { Status = SignalStatus.Resolved, ResolutionRef = resolutionRef };
    }

    public Task<int> ExpireDueAsync(CancellationToken ct) =>
        ExecuteAsync("""
            UPDATE signal
               SET status = @expired
             WHERE expires_at_utc <= @now AND status NOT IN (@resolved, @expired);
            """, ct,
            ("@expired", SignalStatus.Expired), ("@now", Iso(_clock.UtcNow)),
            ("@resolved", SignalStatus.Resolved));

    public async Task<Signal?> GetAsync(string signalId, CancellationToken ct)
    {
        IReadOnlyList<Signal> found = await ReadAsync(
            $"{Select} WHERE id = @id;", ct, ("@id", signalId)).ConfigureAwait(false);

        return found.Count == 0 ? null : found[0];
    }

    public Task<IReadOnlyList<Signal>> PendingAsync(CancellationToken ct) =>
        ReadAsync($"""
            {Select}
             WHERE status IN (@new, @queued, @focused) AND expires_at_utc > @now
             ORDER BY CASE severity
                        WHEN 'CRITICAL' THEN 0 WHEN 'HIGH' THEN 1 WHEN 'MEDIUM' THEN 2
                        WHEN 'LOW' THEN 3 ELSE 4 END,
                      urgency DESC, created_at_utc;
            """, ct,
            ("@new", SignalStatus.New), ("@queued", SignalStatus.Queued),
            ("@focused", SignalStatus.Focused), ("@now", Iso(_clock.UtcNow)));

    // ---- plumbing ----

    /// <summary>
    /// Whether the claimed source is something that actually happened.
    /// </summary>
    /// <remarks>
    /// A committed event, a recorded schedule run, or a stored observation. Those are the three
    /// things RFC 030 rule 1 accepts — "a verifiable event, schedule or health state" — and each is
    /// checked against the table that holds it rather than taken on the classifier's word.
    /// </remarks>
    private async Task<bool> SourceExistsAsync(string sourceEventRef, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceEventRef))
        {
            return false;
        }

        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM domain_event WHERE event_id = @ref)
                 + (SELECT COUNT(*) FROM schedule_run WHERE id = @ref)
                 + (SELECT COUNT(*) FROM observation WHERE id = @ref);
            """;
        command.Parameters.AddWithValue("@ref", sourceEventRef);

        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
    }

    private async Task<int> CountRecentAsync(string dedupeKey, DateTimeOffset since, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM signal WHERE dedupe_key = @k AND created_at_utc >= @since;";
        command.Parameters.AddWithValue("@k", dedupeKey);
        command.Parameters.AddWithValue("@since", Iso(since));

        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    private Task SetStatusAsync(
        string signalId, string status, IReadOnlyList<string> reasons, string? level, CancellationToken ct) =>
        ExecuteAsync("""
            UPDATE signal
               SET status = @s, reason_codes = @r,
                   interruptibility = COALESCE(@level, interruptibility)
             WHERE id = @id;
            """, ct,
            ("@s", status), ("@r", string.Join(',', reasons)),
            ("@level", (object?)level ?? DBNull.Value), ("@id", signalId));

    private async Task<Signal> RequireAsync(string signalId, CancellationToken ct) =>
        await GetAsync(signalId, ct).ConfigureAwait(false)
        ?? throw new SignalException("Unknown signal.");

    private const string Select = """
        SELECT id, source_event_ref, kind, severity, urgency, relevance, confidence, target_refs,
               created_at_utc, expires_at_utc, interruptibility, status, reason_codes, policy_refs,
               resolution_ref
          FROM signal
        """;

    private async Task<IReadOnlyList<Signal>> ReadAsync(
        string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((var name, var value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var signals = new List<Signal>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            signals.Add(new Signal(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6),
                Split(reader.GetString(7)), reader.GetString(8), reader.GetString(9),
                reader.GetString(10), reader.GetString(11),
                Split(reader.GetString(12)), Split(reader.GetString(13)),
                reader.IsDBNull(14) ? null : reader.GetString(14)));
        }

        return signals;
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

    private static IReadOnlyList<string> Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static double Clamp(double value) => Math.Clamp(value, 0, 1);

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
