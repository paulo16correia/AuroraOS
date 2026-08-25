using System.Globalization;
using System.Text.Json;
using Aurora.Adapters.Persistence;
using Aurora.Core;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Self;

/// <summary>
/// What Aurora knows about itself, observed rather than assumed (RFC 027).
/// </summary>
/// <remarks>
/// Every field is read from something that can be checked: the registry says what is installed, the
/// policy engine says what is permitted, the resource model says what there is room for, the health
/// service says what is working. Nothing here asks Aurora how it feels.
/// </remarks>
public sealed class SqliteSelfModel : ISelfModel
{
    /// <summary>
    /// How stale a reading may be before it stops being trusted for a decision.
    /// </summary>
    /// <remarks>
    /// RFC 027's limit case: execution revalidates permissions and health rather than relying on
    /// the snapshot. This is the point past which the snapshot is not even a starting guess.
    /// </remarks>
    private static readonly TimeSpan ReadingStaleAfter = TimeSpan.FromMinutes(2);

    private readonly SqliteConnectionFactory _factory;
    private readonly ICapabilityRegistry _registry;
    private readonly IPolicyEngine _policy;
    private readonly IResourceModel _resources;
    private readonly IHealthService _health;
    private readonly IIdempotencyStore _idempotency;
    private readonly IEventBus _bus;
    private readonly IClock _clock;

    public SqliteSelfModel(
        SqliteConnectionFactory factory,
        ICapabilityRegistry registry,
        IPolicyEngine policy,
        IResourceModel resources,
        IHealthService health,
        IIdempotencyStore idempotency,
        IClock clock, IEventBus bus)
    {
        _factory = factory;
        _registry = registry;
        _policy = policy;
        _resources = resources;
        _health = health;
        _idempotency = idempotency;
        _clock = clock;
        _bus = bus;
    }

    public async Task<SelfModel> RefreshAsync(string mindId, CancellationToken ct)
    {
        DateTimeOffset now = _clock.UtcNow;
        SelfModel? previous = await CurrentAsync(ct).ConfigureAwait(false);

        IReadOnlyList<HealthCheck> checks = await _health.ReadAsync(ct).ConfigureAwait(false);
        ResourceState resources = await _resources.ObserveAsync(ct).ConfigureAwait(false);
        IReadOnlyList<string> unknown = await _idempotency.ListUnknownAsync(ct).ConfigureAwait(false);
        IReadOnlyList<string> running = await RunningCyclesAsync(ct).ConfigureAwait(false);

        var overall = HealthStatus.Worst(checks.Select(c => c.Status));

        // A paused instance stays paused through a refresh: pausing is a decision somebody made,
        // and an observation is not entitled to overturn it.
        var state = previous?.OperationalState == OperationalState.Paused
            ? OperationalState.Paused
            : Determine(overall, unknown.Count, running.Count);

        var capabilities = new CapabilitySnapshot(
            EnabledCapabilities: _registry.List(null).Select(d => d.ActionId).ToList(),

            // Limit case: installed but revoked shows as absent capacity, not as a capability with
            // a caveat. A plan built on it would be a plan that cannot run.
            DisabledCapabilities: [],
            LimitsJson: AuroraJson.Serialize(
                new { max_input_bytes = AuroraLimits.MaxInputBytes, policy = _policy.Version }),
            ProviderStatuses: checks.Select(c => $"{c.Component}:{c.Status}").ToList(),
            CapturedAtUtc: Iso(now));

        var resourceSnapshot = new ResourceSnapshot(
            resources.CpuPct, resources.MemoryPct, resources.DiskPct,
            resources.QueueDepth, resources.ModelCostToday, resources.NetworkState,
            MaintenanceWindow: null, Iso(now));

        var model = new SelfModel(
            Guid.NewGuid().ToString("N"), mindId, (previous?.Version ?? 0) + 1,
            IdentityRef: $"mind/{mindId}", PersonalityRef: null,
            capabilities, resourceSnapshot, state, running,
            CurrentFocusRef: running.Count == 1 ? running[0] : null,
            HealthSummary: Summarise(checks),

            // Rule 4: dated. Health is observed at a moment and stops being current; it is never
            // presumed good because nothing complained.
            HealthObservedAtUtc: Iso(now),
            RecentActivityRefs: previous?.ActiveCycleIds ?? [],
            ObservedAtUtc: Iso(now),
            PausedReason: previous?.PausedReason);

        await SaveAsync(model, ct).ConfigureAwait(false);

        // Published on a transition and never on a reading. Self refreshes on every capability
        // check, and an event per reading would make the bus a log of Aurora looking at itself —
        // which is noise that would bury the one moment somebody actually needed to hear about.
        if (previous is null || previous.OperationalState != model.OperationalState)
        {
            await _bus.PublishAsync(
                new OutboxWrite(
                    EventCatalogue.OperationalStateChanged, 1, EventCatalogue.Producers.Self,
                    Guid.NewGuid().ToString("N"), Sensitivity.Private,
                    AggregateRef: $"mind/{mindId}",
                    PayloadJson: AuroraJson.Serialize(
                        new { from = previous?.OperationalState, to = model.OperationalState }),
                    IdempotencyKey: $"self:{mindId}:{model.Version}"),
                ct).ConfigureAwait(false);
        }

        return model;
    }

    /// <summary>
    /// Works out the operational state from what was observed.
    /// </summary>
    /// <remarks>
    /// RECOVERING while reservations sit in UNKNOWN, because a restart that left calls in an
    /// indeterminate state has not finished starting — whatever the process thinks. Rule 4 again:
    /// DEGRADED comes from a failing check and not from an absence of good news.
    /// </remarks>
    private static string Determine(string health, int unknownReservations, int runningCycles) =>
        (health, unknownReservations, runningCycles) switch
        {
            (_, > 0, _) => OperationalState.Recovering,

            // Only FAIL degrades. A WARN means something warrants a look — a dead letter, a sealed
            // audit chain — and treating that as diminished capacity would stop Aurora acting for
            // reasons that have nothing to do with whether it can. The warning still reaches the
            // health summary, where a person can see it.
            (HealthStatus.Fail, _, _) => OperationalState.Degraded,

            (_, _, > 0) => OperationalState.Busy,
            _ => OperationalState.Ready,
        };

    public async Task<SafeSelfDescription> DescribeAsync(
        MemoryAccessContext access, CancellationToken ct)
    {
        SelfModel model = await CurrentAsync(ct).ConfigureAwait(false)
            ?? await RefreshAsync("local", ct).ConfigureAwait(false);

        var can = new List<string>();
        var cannot = new List<string>();

        foreach (CapabilityDescriptor descriptor in _registry.List(null))
        {
            // Rule 3 in the small: what is said is what the capability does, from its own
            // description. Nothing here reaches a provider name, an endpoint or a credential.
            (descriptor.Effects.Count == 0 || !OperationalState.Holds(model.OperationalState)
                ? can
                : cannot).Add($"{descriptor.ActionId}: {descriptor.Title}");
        }

        if (OperationalState.Holds(model.OperationalState))
        {
            cannot.Add($"start anything on its own while {model.OperationalState}");
        }

        return new SafeSelfDescription(
            model.OperationalState, can, cannot, model.HealthSummary,
            model.HealthObservedAtUtc, model.ActiveCycleIds.Count, model.ObservedAtUtc);
    }

    public async Task<CapabilityAssessment> CanAsync(
        string actionId, Principal principal, CancellationToken ct)
    {
        // Rule 2. Three questions, answered separately, because a connector can be installed and
        // revoked, permitted and out of budget, or safe and not installed at all. Collapsing them
        // is how "I can do that" becomes a promise Aurora cannot keep.
        if (!_registry.TryGet(actionId, out ICapability? capability))
        {
            return new CapabilityAssessment(actionId, false, false, false, "not installed");
        }

        CapabilityDescriptor descriptor = capability.Descriptor;

        PolicyDecision policy = _policy.Evaluate(descriptor, EmptyInput, principal);
        var permitted = policy.Allowed;

        SelfModel model = await CurrentAsync(ct).ConfigureAwait(false)
            ?? await RefreshAsync("local", ct).ConfigureAwait(false);

        // Limit case: an outdated reading is not leant on. A stale Self is refreshed rather than
        // trusted, because permissions and health are exactly what moves between readings.
        if (Parse(model.ObservedAtUtc) + ReadingStaleAfter <= _clock.UtcNow)
        {
            model = await RefreshAsync(model.MindId, ct).ConfigureAwait(false);
        }

        var (safe, reason) = Safety(model, descriptor);

        return new CapabilityAssessment(
            actionId, Installed: true, permitted, safe,
            permitted
                ? reason
                : $"policy refuses this: {policy.Reason ?? "no reason given"}");
    }

    /// <summary>Whether now is a safe moment for this, separately from whether it is allowed.</summary>
    private static (bool Safe, string Reason) Safety(SelfModel model, CapabilityDescriptor descriptor)
    {
        if (model.OperationalState == OperationalState.Paused)
        {
            return (false, $"paused: {model.PausedReason ?? "no reason recorded"}");
        }

        if (model.OperationalState == OperationalState.Recovering)
        {
            return (false, "recovering; calls left indeterminate by a restart are still being settled");
        }

        // Reading remains safe on a degraded instance; reaching outside it does not. That is the
        // whole of "I can prepare, but not send".
        if (model.OperationalState == OperationalState.Degraded && descriptor.Effects.Count > 0)
        {
            return (false, $"degraded ({model.HealthSummary}); nothing that reaches outside Aurora");
        }

        return (true, "installed, permitted and safe now");
    }

    public async Task<SelfModel> PauseAsync(string actor, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(reason))
        {
            throw new SelfException("Pausing records who did it and why.");
        }

        SelfModel model = await RefreshAsync(
            (await CurrentAsync(ct).ConfigureAwait(false))?.MindId ?? "local", ct).ConfigureAwait(false);

        SelfModel paused = model with
        {
            Id = Guid.NewGuid().ToString("N"),
            Version = model.Version + 1,
            OperationalState = OperationalState.Paused,
            PausedReason = $"{reason} (by {actor})",
            ObservedAtUtc = Iso(_clock.UtcNow),
        };

        await SaveAsync(paused, ct).ConfigureAwait(false);
        return paused;
    }

    public async Task<SelfModel> ResumeAsync(string actor, CancellationToken ct)
    {
        SelfModel model = await CurrentAsync(ct).ConfigureAwait(false)
            ?? throw new SelfException("Nothing has been observed yet; there is nothing to resume.");

        if (model.OperationalState != OperationalState.Paused)
        {
            throw new SelfException($"Aurora is {model.OperationalState}, not paused.");
        }

        SelfModel resumed = model with
        {
            Id = Guid.NewGuid().ToString("N"),
            Version = model.Version + 1,
            OperationalState = OperationalState.Booting,
            PausedReason = null,
            ObservedAtUtc = Iso(_clock.UtcNow),
        };

        await SaveAsync(resumed, ct).ConfigureAwait(false);

        // Resuming does not assert that everything is fine; it takes a fresh reading and reports
        // whatever that says, which may be DEGRADED or RECOVERING.
        return await RefreshAsync(model.MindId, ct).ConfigureAwait(false);
    }

    public async Task<SelfModel?> CurrentAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, mind_id, version, identity_ref, personality_ref, capability_snapshot_json,
                   resource_snapshot_json, operational_state, active_cycle_ids, current_focus_ref,
                   health_summary, health_observed_at_utc, recent_activity_refs, observed_at_utc,
                   paused_reason
              FROM self_model ORDER BY version DESC LIMIT 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new SelfModel(
            reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            AuroraJson.Deserialize<CapabilitySnapshot>(reader.GetString(5)),
            AuroraJson.Deserialize<ResourceSnapshot>(reader.GetString(6)),
            reader.GetString(7), Lines(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetString(10), reader.GetString(11), Lines(reader.GetString(12)),
            reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14));
    }

    // ---- plumbing ----

    private static readonly JsonElement EmptyInput = JsonDocument.Parse("{}").RootElement.Clone();

    private static string Summarise(IReadOnlyList<HealthCheck> checks)
    {
        var failing = checks.Where(c => c.Status != HealthStatus.Pass).ToList();

        return failing.Count == 0
            ? "every component reports passing"
            : string.Join("; ", failing.Select(c => $"{c.Component} {c.Status}: {c.DetailSafe}"));
    }

    private async Task<IReadOnlyList<string>> RunningCyclesAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM cognitive_cycle WHERE status = @running ORDER BY started_at_utc;";
        command.Parameters.AddWithValue("@running", CycleStatus.Running);

        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private Task SaveAsync(SelfModel model, CancellationToken ct) =>
        ExecuteAsync("""
            INSERT INTO self_model
                (id, mind_id, version, identity_ref, personality_ref, capability_snapshot_json,
                 resource_snapshot_json, operational_state, active_cycle_ids, current_focus_ref,
                 health_summary, health_observed_at_utc, recent_activity_refs, observed_at_utc,
                 paused_reason)
            VALUES (@id, @mind, @version, @identity, @personality, @capabilities, @resources,
                    @state, @cycles, @focus, @health, @observed, @recent, @at, @paused);
            """, ct,
            ("@id", model.Id), ("@mind", model.MindId), ("@version", model.Version),
            ("@identity", model.IdentityRef),
            ("@personality", (object?)model.PersonalityRef ?? DBNull.Value),
            ("@capabilities", AuroraJson.Serialize(model.Capabilities)),
            ("@resources", AuroraJson.Serialize(model.Resources)),
            ("@state", model.OperationalState),
            ("@cycles", string.Join('\n', model.ActiveCycleIds)),
            ("@focus", (object?)model.CurrentFocusRef ?? DBNull.Value),
            ("@health", model.HealthSummary), ("@observed", model.HealthObservedAtUtc),
            ("@recent", string.Join('\n', model.RecentActivityRefs)),
            ("@at", model.ObservedAtUtc),
            ("@paused", (object?)model.PausedReason ?? DBNull.Value));

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
