using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Cryptography;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Incidents;

/// <summary>
/// Incidents, and the three things RFC 09 rule 5 requires of a high-risk one.
/// </summary>
/// <remarks>
/// Containment is targeted where the event named what it affected and total where it did not.
/// Revoking every consent session for a plugin that misbehaved would punish the owner for the
/// plugin; revoking nothing because the event named no resource would be worse.
/// <para>
/// Every step is best-effort and every outcome is written down, including the failures. An incident
/// response that throws halfway leaves the system in a state nobody recorded — so a revocation that
/// fails is a line on the incident, not an exception that abandons the other two steps.
/// </para>
/// </remarks>
public sealed class SqliteIncidentService : IIncidentService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly IConsentSessionStore _sessions;
    private readonly IToolManager _tools;
    private readonly IPluginRegistry _plugins;
    private readonly IAuditStore _audit;
    private readonly IEventBus _bus;
    private readonly IOperatorPrompt _prompt;
    private readonly IClock _clock;

    public SqliteIncidentService(
        SqliteConnectionFactory factory,
        IConsentSessionStore sessions,
        IToolManager tools,
        IPluginRegistry plugins,
        IAuditStore audit,
        IEventBus bus,
        IOperatorPrompt prompt,
        IClock clock)
    {
        _factory = factory;
        _sessions = sessions;
        _tools = tools;
        _plugins = plugins;
        _audit = audit;
        _bus = bus;
        _prompt = prompt;
        _clock = clock;
    }

    public async Task<Incident> OpenAsync(SecurityEvent securityEvent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(securityEvent.EvidenceRef))
        {
            // Rule 5 asks for evidence to be preserved. An incident that cites none preserves
            // nothing, and would leave whoever reads it later with an assertion and no way to check.
            throw new IncidentException("A security event must cite evidence.");
        }

        DateTimeOffset now = _clock.UtcNow;
        var highRisk = SecuritySeverity.IsHighRisk(securityEvent.Severity);

        // Revoke first. The notification comes after, because the seconds spent drawing a dialog
        // are seconds the thing is still running.
        IReadOnlyList<string> actions = highRisk
            ? await ContainAsync(securityEvent, ct).ConfigureAwait(false)
            : [];

        var incident = new Incident(
            Guid.NewGuid().ToString("N"),
            securityEvent with { Id = Guid.NewGuid().ToString("N"), DetectedAtUtc = Iso(now) },
            highRisk ? IncidentStatus.Contained : IncidentStatus.Open,
            actions,
            Iso(now),
            highRisk ? Iso(now) : null,
            null,
            null);

        await SaveAsync(incident, ct).ConfigureAwait(false);

        // Preserve evidence: the audit chain is the one record here that cannot be edited without
        // being detected, so the citation goes there as well as on the incident row.
        await _audit.AppendAsync(
            new AuditEntry(
                securityEvent.ActorRef, securityEvent.ActorRef,
                "security.incident",
                Hashing.Sha256Hex(securityEvent.EvidenceRef),
                highRisk ? "contained" : "recorded",
                Risk: securityEvent.Severity,
                Via: securityEvent.Type,
                Decision: string.Join("; ", actions),
                Reason: securityEvent.ResourceRef),
            ct).ConfigureAwait(false);

        await _bus.PublishAsync(
            new OutboxWrite(
                EventCatalogue.SecurityIncidentOpened, 1, EventCatalogue.Producers.Security,
                securityEvent.CorrelationId, Sensitivity.Private,
                AggregateRef: $"incident/{incident.Id}",
                PayloadJson: AuroraJson.Serialize(new
                {
                    severity = securityEvent.Severity,
                    type = securityEvent.Type,
                    status = incident.Status,
                    contained = actions.Count,
                }),
                IdempotencyKey: $"incident:{incident.Id}"),
            ct).ConfigureAwait(false);

        if (highRisk)
        {
            // Notify last, and only for a high-risk one. An alert per recorded event is an alert
            // people turn off, and then the one that mattered arrives silenced.
            await _prompt.NotifyAsync(
                "Aurora contained a security incident",
                $"{securityEvent.Type} ({securityEvent.Severity}). "
                + (actions.Count > 0
                    ? $"Revoked: {string.Join("; ", actions)}."
                    : "Nothing could be revoked automatically."),
                ct).ConfigureAwait(false);
        }

        return incident;
    }

    /// <summary>
    /// Revokes what the event named, and the owner's standing consent either way.
    /// </summary>
    private async Task<IReadOnlyList<string>> ContainAsync(
        SecurityEvent securityEvent, CancellationToken ct)
    {
        var actions = new List<string>();

        // A consent session is a standing permission to act without asking again. During an
        // incident that is exactly the thing that should not still be true, whatever was affected.
        actions.Add(await TryAsync(
            async () => $"revoked {await _sessions.RevokeAllAsync(ct).ConfigureAwait(false)} consent session(s)",
            "consent sessions could not be revoked").ConfigureAwait(false));

        var resource = securityEvent.ResourceRef;

        if (resource.StartsWith("tool/", StringComparison.Ordinal))
        {
            var toolId = resource["tool/".Length..];

            actions.Add(await TryAsync(
                async () =>
                {
                    await _tools.DisableAsync(toolId, $"incident: {securityEvent.Type}", ct)
                        .ConfigureAwait(false);

                    return $"disabled tool {toolId}";
                },
                $"tool {toolId} could not be disabled").ConfigureAwait(false));
        }
        else if (resource.StartsWith("plugin/", StringComparison.Ordinal))
        {
            actions.Add(await TryAsync(
                async () =>
                {
                    PluginInstallation? installed =
                        await _plugins.GetAsync(resource, ct).ConfigureAwait(false);

                    if (installed is null)
                    {
                        return $"plugin {resource} is not installed";
                    }

                    await _plugins.DisableAsync(installed.Id, "incident", ct).ConfigureAwait(false);
                    return $"disabled plugin {resource}";
                },
                $"plugin {resource} could not be disabled").ConfigureAwait(false));
        }

        return actions;
    }

    /// <summary>
    /// Runs one containment step and turns its failure into a recorded line.
    /// </summary>
    /// <remarks>
    /// A step that throws must not abandon the steps after it. The whole value of this method is
    /// that the incident says what was and was not achieved, rather than stopping at the first
    /// thing that did not work and leaving the rest unattempted and unrecorded.
    /// </remarks>
    private static async Task<string> TryAsync(Func<Task<string>> step, string onFailure)
    {
        try
        {
            return await step().ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return $"{onFailure} ({failure.GetType().Name})";
        }
    }

    public async Task<Incident> ResolveAsync(
        string incidentId, string resolution, string actor, CancellationToken ct)
    {
        Incident incident = await GetAsync(incidentId, ct).ConfigureAwait(false)
            ?? throw new IncidentException("Unknown incident.");

        if (incident.Status == IncidentStatus.Resolved)
        {
            throw new IncidentException("This incident is already resolved.");
        }

        if (string.IsNullOrWhiteSpace(resolution))
        {
            throw new IncidentException("An incident is resolved with a reason, not silently.");
        }

        var at = Iso(_clock.UtcNow);

        await ExecuteAsync(
            "UPDATE incident SET status = @s, resolved_at_utc = @at, resolution = @r WHERE id = @id;",
            ct,
            ("@s", IncidentStatus.Resolved), ("@at", at), ("@r", resolution), ("@id", incidentId))
            .ConfigureAwait(false);

        await _audit.AppendAsync(
            new AuditEntry(
                actor, actor, "security.incident.resolve",
                Hashing.Sha256Hex(incidentId), "resolved",
                Risk: incident.Event.Severity, Via: incident.Event.Type, Reason: resolution),
            ct).ConfigureAwait(false);

        return incident with
        {
            Status = IncidentStatus.Resolved,
            ResolvedAtUtc = at,
            Resolution = resolution,
        };
    }

    public async Task<Incident?> GetAsync(string incidentId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = Select + " WHERE id = @id;";
        command.Parameters.AddWithValue("@id", incidentId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<Incident>> OpenIncidentsAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            Select + " WHERE status <> @resolved ORDER BY opened_at_utc DESC, rowid DESC;";

        command.Parameters.AddWithValue("@resolved", IncidentStatus.Resolved);

        var incidents = new List<Incident>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            incidents.Add(Read(reader));
        }

        return incidents;
    }

    private const string Select = """
        SELECT id, event_id, severity, type, correlation_id, actor_ref, resource_ref, decision_ref,
               evidence_ref, detected_at_utc, status, containment_actions, opened_at_utc,
               contained_at_utc, resolved_at_utc, resolution
          FROM incident
        """;

    private static Incident Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        new SecurityEvent(
            reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8), reader.GetString(9)),
        reader.GetString(10),
        reader.GetString(11).Split('\n', StringSplitOptions.RemoveEmptyEntries),
        reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.IsDBNull(14) ? null : reader.GetString(14),
        reader.IsDBNull(15) ? null : reader.GetString(15));

    private Task SaveAsync(Incident incident, CancellationToken ct) =>
        ExecuteAsync("""
            INSERT INTO incident
                (id, event_id, severity, type, correlation_id, actor_ref, resource_ref, decision_ref,
                 evidence_ref, detected_at_utc, status, containment_actions, opened_at_utc,
                 contained_at_utc, resolved_at_utc, resolution)
            VALUES (@id, @eid, @sev, @type, @corr, @actor, @res, @dec, @ev, @det, @status,
                    @actions, @opened, @contained, NULL, NULL);
            """, ct,
            ("@id", incident.Id), ("@eid", incident.Event.Id), ("@sev", incident.Event.Severity),
            ("@type", incident.Event.Type), ("@corr", incident.Event.CorrelationId),
            ("@actor", incident.Event.ActorRef), ("@res", incident.Event.ResourceRef),
            ("@dec", (object?)incident.Event.DecisionRef ?? DBNull.Value),
            ("@ev", incident.Event.EvidenceRef), ("@det", incident.Event.DetectedAtUtc),
            ("@status", incident.Status),
            ("@actions", string.Join('\n', incident.ContainmentActions)),
            ("@opened", incident.OpenedAtUtc),
            ("@contained", (object?)incident.ContainedAtUtc ?? DBNull.Value));

    private async Task ExecuteAsync(
        string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((var name, var value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
