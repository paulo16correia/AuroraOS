using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Capability;

/// <summary>Chooses a permitted realisation for a stated capability (RFC 051).</summary>
public sealed class SqliteCapabilityResolver : ICapabilityResolver
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteCapabilityResolver(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<CapabilityDefinition> RegisterCapabilityAsync(
        CapabilityDefinition definition, CancellationToken ct)
    {
        await ExecuteAsync("""
            INSERT INTO capability_definition
                (id, domain, intent_schema, effect_classes, risk_class, required_permissions)
            VALUES (@id, @d, @schema, @effects, @risk, @perms)
            ON CONFLICT(id) DO UPDATE SET
                domain = excluded.domain, intent_schema = excluded.intent_schema,
                effect_classes = excluded.effect_classes, risk_class = excluded.risk_class,
                required_permissions = excluded.required_permissions;
            """, ct,
            ("@id", definition.Id), ("@d", definition.Domain), ("@schema", definition.IntentSchema),
            ("@effects", string.Join(',', definition.EffectClasses)), ("@risk", definition.RiskClass),
            ("@perms", string.Join(',', definition.RequiredPermissions))).ConfigureAwait(false);

        return definition;
    }

    public async Task<CapabilityProvider> RegisterProviderAsync(
        CapabilityProvider provider, CancellationToken ct)
    {
        CapabilityDefinition capability =
            await GetCapabilityAsync(provider.CapabilityId, ct).ConfigureAwait(false)
            ?? throw new CapabilityResolutionException("Unknown capability.");

        // Rule 3: a provider cannot offer effects outside the capability's manifest. Refusing at
        // registration means the manifest stays the honest description of what can happen.
        var excess = provider.DeclaredEffects
            .Except(capability.EffectClasses, StringComparer.Ordinal).ToList();

        if (excess.Count > 0)
        {
            throw new CapabilityResolutionException(
                $"Provider declares effects outside the capability manifest: {string.Join(", ", excess)}.");
        }

        await ExecuteAsync("""
            INSERT INTO capability_provider
                (id, capability_id, application_id, tool_ref, priority, available, cost_estimate,
                 data_classes, constraints, declared_effects, health_ref)
            VALUES (@id, @cap, @app, @tool, @pri, @avail, @cost, @classes, @cons, @effects, @health)
            ON CONFLICT(id) DO UPDATE SET
                priority = excluded.priority, available = excluded.available,
                cost_estimate = excluded.cost_estimate, data_classes = excluded.data_classes,
                constraints = excluded.constraints, declared_effects = excluded.declared_effects,
                health_ref = excluded.health_ref;
            """, ct,
            ("@id", provider.Id), ("@cap", provider.CapabilityId), ("@app", provider.ApplicationId),
            ("@tool", provider.ToolRef), ("@pri", provider.Priority),
            ("@avail", provider.Available ? 1 : 0), ("@cost", provider.CostEstimate),
            ("@classes", string.Join(',', provider.DataClasses)),
            ("@cons", string.Join(',', provider.Constraints)),
            ("@effects", string.Join(',', provider.DeclaredEffects)),
            ("@health", (object?)provider.HealthRef ?? DBNull.Value)).ConfigureAwait(false);

        return provider;
    }

    public async Task<CapabilityRequest> ResolveAsync(
        CapabilityRequest request, ResolutionContext context, CancellationToken ct)
    {
        CapabilityDefinition capability =
            await GetCapabilityAsync(request.CapabilityId, ct).ConfigureAwait(false)
            ?? throw new CapabilityResolutionException($"Unknown capability '{request.CapabilityId}'.");

        IReadOnlyList<CapabilityProvider> providers =
            await ProvidersAsync(request.CapabilityId, ct).ConfigureAwait(false);

        var verdicts = new List<ProviderVerdict>();
        var eligible = new List<CapabilityProvider>();

        foreach (CapabilityProvider provider in providers)
        {
            var reason = Judge(provider, capability, request, context);
            var isEligible = reason is null;

            verdicts.Add(new ProviderVerdict(provider.Id, isEligible, reason ?? ResolutionReason.Chosen));

            if (isEligible)
            {
                eligible.Add(provider);
            }
        }

        CapabilityRequest resolved;
        if (eligible.Count == 0)
        {
            // Limit case: no provider means BLOCKED, naming what is missing. It never degrades into
            // a generic shell call, which is how a capability system stops being one.
            var missing = request.PinnedProviderId is { } pinned
                ? $"the requested provider '{pinned}' is not available or not permitted"
                : $"no permitted provider realises '{capability.Id}'";

            resolved = request with
            {
                Status = CapabilityRequestStatus.Blocked,
                BlockedReason = missing,
                ResolvedProviderId = null,
            };
        }
        else
        {
            // Preference leans, it does not override: it only orders providers that already passed
            // permission, cost and constraint checks.
            CapabilityProvider chosen = eligible
                .OrderByDescending(p => p.Id == request.PreferredProviderId)
                .ThenBy(p => p.Priority)
                .ThenBy(p => p.CostEstimate)
                .ThenBy(p => p.Id, StringComparer.Ordinal)
                .First();

            for (var i = 0; i < verdicts.Count; i++)
            {
                if (verdicts[i].Eligible && verdicts[i].ProviderId != chosen.Id)
                {
                    verdicts[i] = verdicts[i] with { Reason = ResolutionReason.LowerPriority };
                }
            }

            resolved = request with
            {
                Status = CapabilityRequestStatus.Resolved,
                ResolvedProviderId = chosen.Id,
                BlockedReason = null,
            };
        }

        await SaveRequestAsync(resolved, ct).ConfigureAwait(false);
        await SaveVerdictsAsync(resolved.Id, verdicts, Explain(resolved, capability), ct).ConfigureAwait(false);

        return resolved;
    }

    public async Task<CapabilityRequest> HandleProviderFailureAsync(
        string requestId, string reason, ResolutionContext context, CancellationToken ct)
    {
        CapabilityRequest request = await GetRequestAsync(requestId, ct).ConfigureAwait(false)
            ?? throw new CapabilityResolutionException("Unknown request.");

        // Rule 4 and its limit case: a pinned destination is the intention. Email being down does
        // not make Discord an acceptable substitute, however available it is.
        if (request.PinnedProviderId is not null)
        {
            CapabilityRequest blocked = request with
            {
                Status = CapabilityRequestStatus.Blocked,
                BlockedReason =
                    $"{reason}; the request named a specific provider, so no alternative preserves the intention",
                ResolvedProviderId = null,
            };

            await SaveRequestAsync(blocked, ct).ConfigureAwait(false);
            return blocked;
        }

        // Otherwise an alternative is allowed only if it passes the same checks the original did.
        var failed = request.ResolvedProviderId;
        CapabilityRequest retried = await ResolveAsync(
            request with
            {
                Status = CapabilityRequestStatus.Requested,
                ResolvedProviderId = null,
                TargetConstraints = request.TargetConstraints,
            },
            context, ct).ConfigureAwait(false);

        if (retried.ResolvedProviderId == failed)
        {
            CapabilityRequest blocked = retried with
            {
                Status = CapabilityRequestStatus.Blocked,
                BlockedReason = $"{reason}; the only permitted provider is the one that failed",
                ResolvedProviderId = null,
            };

            await SaveRequestAsync(blocked, ct).ConfigureAwait(false);
            return blocked;
        }

        return retried;
    }

    public async Task<ResolutionReport> ExplainResolutionAsync(string requestId, CancellationToken ct)
    {
        CapabilityRequest request = await GetRequestAsync(requestId, ct).ConfigureAwait(false)
            ?? throw new CapabilityResolutionException("Unknown request.");

        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT provider_id, eligible, reason, explanation FROM resolution_verdict WHERE request_id = @r;";
        command.Parameters.AddWithValue("@r", requestId);

        var verdicts = new List<ProviderVerdict>();
        var explanation = string.Empty;

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            verdicts.Add(new ProviderVerdict(reader.GetString(0), reader.GetInt32(1) == 1, reader.GetString(2)));
            explanation = reader.GetString(3);
        }

        return new ResolutionReport(requestId, request.ResolvedProviderId, verdicts, explanation);
    }

    public async Task<CapabilityRequest?> GetRequestAsync(string requestId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, decision_ref, capability_id, intent_payload_json, target_constraints, status,
                   pinned_provider_id, preferred_provider_id, resolved_provider_id, blocked_reason
              FROM capability_request WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@id", requestId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new CapabilityRequest(
            reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2), reader.GetString(3), Split(reader.GetString(4)), reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));
    }

    // ---- internals ----

    /// <summary>Returns null when the provider is eligible, or the reason it is not (rule 2).</summary>
    private static string? Judge(
        CapabilityProvider provider,
        CapabilityDefinition capability,
        CapabilityRequest request,
        ResolutionContext context)
    {
        if (request.PinnedProviderId is { } pinned && provider.Id != pinned)
        {
            return ResolutionReason.NotThePinnedProvider;
        }

        if (!provider.Available)
        {
            return ResolutionReason.Unavailable;
        }

        var missing = capability.RequiredPermissions
            .Except(context.GrantedPermissions, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            return ResolutionReason.MissingPermission;
        }

        // Re-checked at resolution as well as registration: a manifest can be edited after a
        // provider was registered against it.
        if (provider.DeclaredEffects.Except(capability.EffectClasses, StringComparer.Ordinal).Any())
        {
            return ResolutionReason.EffectsExceedManifest;
        }

        if (provider.DataClasses.Except(context.AllowedDataClasses, StringComparer.Ordinal).Any())
        {
            return ResolutionReason.ConstraintUnmet;
        }

        var unmet = request.TargetConstraints
            .Except(provider.Constraints, StringComparer.Ordinal).ToList();
        if (unmet.Count > 0)
        {
            return ResolutionReason.ConstraintUnmet;
        }

        // A preference cannot buy its way past the cost ceiling.
        return provider.CostEstimate > context.CostCeiling
            ? ResolutionReason.OverCostCeiling
            : null;
    }

    private static string Explain(CapabilityRequest request, CapabilityDefinition capability) =>
        request.Status == CapabilityRequestStatus.Resolved
            ? $"'{capability.Id}' resolved to provider '{request.ResolvedProviderId}'."
            : $"'{capability.Id}' was not resolved: {request.BlockedReason}";

    private async Task<CapabilityDefinition?> GetCapabilityAsync(string id, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, domain, intent_schema, effect_classes, risk_class, required_permissions
              FROM capability_definition WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@id", id);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new CapabilityDefinition(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                Split(reader.GetString(3)), reader.GetString(4), Split(reader.GetString(5)))
            : null;
    }

    private async Task<IReadOnlyList<CapabilityProvider>> ProvidersAsync(
        string capabilityId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, capability_id, application_id, tool_ref, priority, available, cost_estimate,
                   data_classes, constraints, declared_effects, health_ref
              FROM capability_provider WHERE capability_id = @c ORDER BY priority ASC, id ASC;
            """;
        command.Parameters.AddWithValue("@c", capabilityId);

        var rows = new List<CapabilityProvider>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new CapabilityProvider(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), reader.GetInt32(5) == 1, reader.GetDouble(6),
                Split(reader.GetString(7)), Split(reader.GetString(8)), Split(reader.GetString(9)),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        return rows;
    }

    private Task SaveRequestAsync(CapabilityRequest r, CancellationToken ct) =>
        ExecuteAsync("""
            INSERT INTO capability_request
                (id, decision_ref, capability_id, intent_payload_json, target_constraints, status,
                 pinned_provider_id, preferred_provider_id, resolved_provider_id, blocked_reason)
            VALUES (@id, @dec, @cap, @payload, @cons, @status, @pin, @pref, @resolved, @blocked)
            ON CONFLICT(id) DO UPDATE SET
                status = excluded.status, resolved_provider_id = excluded.resolved_provider_id,
                blocked_reason = excluded.blocked_reason;
            """, ct,
            ("@id", r.Id), ("@dec", (object?)r.DecisionRef ?? DBNull.Value),
            ("@cap", r.CapabilityId), ("@payload", r.IntentPayloadJson),
            ("@cons", string.Join(',', r.TargetConstraints)), ("@status", r.Status),
            ("@pin", (object?)r.PinnedProviderId ?? DBNull.Value),
            ("@pref", (object?)r.PreferredProviderId ?? DBNull.Value),
            ("@resolved", (object?)r.ResolvedProviderId ?? DBNull.Value),
            ("@blocked", (object?)r.BlockedReason ?? DBNull.Value));

    private async Task SaveVerdictsAsync(
        string requestId, IReadOnlyList<ProviderVerdict> verdicts, string explanation, CancellationToken ct)
    {
        await ExecuteAsync("DELETE FROM resolution_verdict WHERE request_id = @r;", ct,
            ("@r", requestId)).ConfigureAwait(false);

        foreach (ProviderVerdict verdict in verdicts)
        {
            await ExecuteAsync("""
                INSERT INTO resolution_verdict (id, request_id, provider_id, eligible, reason, explanation)
                VALUES (@id, @r, @p, @e, @reason, @exp);
                """, ct,
                ("@id", Guid.NewGuid().ToString("N")), ("@r", requestId), ("@p", verdict.ProviderId),
                ("@e", verdict.Eligible ? 1 : 0), ("@reason", verdict.Reason), ("@exp", explanation))
                .ConfigureAwait(false);
        }
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

    private static IReadOnlyList<string> Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries);
}
