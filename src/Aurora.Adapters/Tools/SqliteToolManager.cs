using System.Globalization;
using System.Text.Json;
using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Cryptography;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Tools;

/// <summary>Runs connector calls under a reduced, auditable contract (RFC 06).</summary>
public sealed class SqliteToolManager : IToolManager
{
    /// <summary>Cap on the output a connector may return, so a remote side cannot flood us (rule 4).</summary>
    public const int MaxOutputBytes = 64 * 1024;

    private readonly SqliteConnectionFactory _factory;
    private readonly ISchemaValidator _validator;
    private readonly IVault _vault;
    private readonly IClock _clock;
    private readonly Dictionary<string, IToolConnector> _connectors = new(StringComparer.Ordinal);

    public SqliteToolManager(
        SqliteConnectionFactory factory, ISchemaValidator validator, IVault vault, IClock clock)
    {
        _factory = factory;
        _validator = validator;
        _vault = vault;
        _clock = clock;
    }

    public async Task RegisterAsync(IToolConnector connector, CancellationToken ct)
    {
        ToolManifest manifest = connector.Describe();

        // Rule 1: capabilities are stated explicitly. A tool that declares none offers nothing,
        // which is the opposite of a shell that offers everything.
        if (manifest.Capabilities.Count == 0)
        {
            throw new ToolException($"'{manifest.ToolId}' declares no capability.");
        }

        _connectors[manifest.ToolId] = connector;

        await ExecuteAsync("""
            INSERT INTO tool_manifest
                (tool_id, version, provider, capabilities, input_schema, output_schema, effects,
                 data_classes_in, data_classes_out, auth_mode, timeout_seconds,
                 rate_limit_per_minute, requires_approval, secret_reference_id, disabled_reason)
            VALUES (@id, @v, @p, @caps, @in, @out, @effects, @dci, @dco, @auth, @timeout,
                    @rate, @approval, @secret, NULL)
            ON CONFLICT(tool_id) DO UPDATE SET
                version = excluded.version, capabilities = excluded.capabilities,
                input_schema = excluded.input_schema, output_schema = excluded.output_schema,
                effects = excluded.effects, timeout_seconds = excluded.timeout_seconds,
                rate_limit_per_minute = excluded.rate_limit_per_minute,
                requires_approval = excluded.requires_approval,
                secret_reference_id = excluded.secret_reference_id, disabled_reason = NULL;
            """, ct,
            ("@id", manifest.ToolId), ("@v", manifest.Version), ("@p", manifest.Provider),
            ("@caps", string.Join(',', manifest.Capabilities)), ("@in", manifest.InputSchema),
            ("@out", manifest.OutputSchema), ("@effects", string.Join(',', manifest.Effects)),
            ("@dci", string.Join(',', manifest.DataClassesIn)),
            ("@dco", string.Join(',', manifest.DataClassesOut)), ("@auth", manifest.AuthMode),
            ("@timeout", manifest.TimeoutSeconds), ("@rate", manifest.RateLimitPerMinute),
            ("@approval", manifest.RequiresApproval ? 1 : 0),
            ("@secret", (object?)manifest.SecretReferenceId ?? DBNull.Value)).ConfigureAwait(false);
    }

    public async Task<ToolCall> ProposeAsync(
        string workItemId, string? taskId, string toolId, string capability,
        string inputJson, string? idempotencyKey, CancellationToken ct)
    {
        (ToolManifest manifest, var disabledReason) = await RequireManifestAsync(toolId, ct).ConfigureAwait(false);

        if (disabledReason is not null)
        {
            throw new ToolException($"'{toolId}' is disabled: {disabledReason}");
        }

        if (!manifest.Capabilities.Contains(capability, StringComparer.Ordinal))
        {
            throw new ToolException($"'{toolId}' does not offer '{capability}'.");
        }

        // Rule 2: a writing tool needs an idempotency key. Without one, a reconcile or a retry has
        // no way to tell "do it" from "do it again".
        if (manifest.IsWriting && string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ToolException($"'{toolId}' has effects and requires an idempotency key.");
        }

        using JsonDocument schema = JsonDocument.Parse(manifest.InputSchema);
        using JsonDocument input = JsonDocument.Parse(inputJson);

        SchemaValidationResult validation = _validator.Validate(schema.RootElement, input.RootElement);
        if (!validation.IsValid)
        {
            throw new ToolException($"Input failed the tool's schema: {string.Join("; ", validation.Errors)}");
        }

        var call = new ToolCall(
            Guid.NewGuid().ToString("N"), workItemId, taskId, toolId, capability,
            Redact(inputJson, manifest), Hashing.Sha256Hex(inputJson), idempotencyKey,
            ToolCallStatus.Proposed, PolicyDecisionIds: [], ApprovalId: null,
            StartedAtUtc: null, EndedAtUtc: null, ExternalReference: null,
            OutputRef: null, ErrorCode: null);

        await SaveAsync(call, ct).ConfigureAwait(false);
        return call;
    }

    public async Task<ToolCall> AuthorizeAsync(
        string callId, IReadOnlyList<string> policyDecisionIds, string? approvalId, CancellationToken ct)
    {
        ToolCall call = await RequireCallAsync(callId, ct).ConfigureAwait(false);
        (ToolManifest manifest, _) = await RequireManifestAsync(call.ToolId, ct).ConfigureAwait(false);

        if (call.Status != ToolCallStatus.Proposed)
        {
            throw new ToolException($"Only a PROPOSED call is authorized; this is {call.Status}.");
        }

        if (policyDecisionIds.Count == 0)
        {
            throw new ToolException("A tool call is authorized by a policy decision, not by asking.");
        }

        if (manifest.RequiresApproval && string.IsNullOrWhiteSpace(approvalId))
        {
            throw new ToolException($"'{call.ToolId}' requires an approval.");
        }

        ToolCall authorized = call with
        {
            Status = ToolCallStatus.Authorized,
            PolicyDecisionIds = policyDecisionIds,
            ApprovalId = approvalId,
        };

        await SaveAsync(authorized, ct).ConfigureAwait(false);
        return authorized;
    }

    public async Task<ToolCall> ExecuteAsync(string callId, CancellationToken ct)
    {
        ToolCall call = await RequireCallAsync(callId, ct).ConfigureAwait(false);
        (ToolManifest manifest, var disabledReason) =
            await RequireManifestAsync(call.ToolId, ct).ConfigureAwait(false);

        if (disabledReason is not null)
        {
            throw new ToolException($"'{call.ToolId}' is disabled: {disabledReason}");
        }

        if (call.Status != ToolCallStatus.Authorized && call.Status != ToolCallStatus.Queued)
        {
            throw new ToolException($"A {call.Status} call is not executed.");
        }

        // Rate limit: defer with a retry_after rather than hammering. RFC 06 forbids tight
        // repetitions, and a queue with a time on it is the difference.
        var recent = await RecentCallsAsync(call.ToolId, ct).ConfigureAwait(false);
        if (manifest.RateLimitPerMinute > 0 && recent >= manifest.RateLimitPerMinute)
        {
            ToolCall queued = call with
            {
                Status = ToolCallStatus.Queued,
                RetryAfterUtc = Iso(_clock.UtcNow.AddMinutes(1)),
            };

            await SaveAsync(queued, ct).ConfigureAwait(false);
            return queued;
        }

        IToolConnector connector = _connectors.TryGetValue(call.ToolId, out var found)
            ? found
            : throw new ToolException($"No connector is registered for '{call.ToolId}'.");

        ToolCall running = call with
        {
            Status = ToolCallStatus.Running,
            StartedAtUtc = Iso(_clock.UtcNow),
            RetryAfterUtc = null,
        };
        await SaveAsync(running, ct).ConfigureAwait(false);

        // Rule 5: the secret is leased for this tool. The vault enforces allowed_tool_ids, so a
        // connector cannot obtain another connector's credential even by asking for its id.
        EphemeralSecretHandle? handle = null;
        try
        {
            if (manifest.AuthMode == AuthMode.VaultSecret && manifest.SecretReferenceId is { } secretRef)
            {
                handle = await _vault.LeaseAsync(
                    secretRef, new ToolCallRef(call.Id, call.ToolId), ct).ConfigureAwait(false);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(manifest.TimeoutSeconds));

            ToolResult result = await connector
                .ExecuteAsync(running, handle, timeout.Token).ConfigureAwait(false);

            return await SettleAsync(running, manifest, result, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The one that matters: we timed out after dispatching. "No response" is not "it did
            // not happen", so this becomes UNKNOWN and waits to be reconciled — never resent.
            ToolCall unknown = running with
            {
                Status = ToolCallStatus.Unknown,
                EndedAtUtc = Iso(_clock.UtcNow),
                ErrorCode = "timeout_after_dispatch",
            };

            await SaveAsync(unknown, ct).ConfigureAwait(false);
            return unknown;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    public async Task<ToolCall> ReconcileAsync(string callId, CancellationToken ct)
    {
        ToolCall call = await RequireCallAsync(callId, ct).ConfigureAwait(false);

        if (call.Status != ToolCallStatus.Unknown)
        {
            throw new ToolException($"Only an UNKNOWN call is reconciled; this is {call.Status}.");
        }

        (ToolManifest manifest, _) = await RequireManifestAsync(call.ToolId, ct).ConfigureAwait(false);
        IToolConnector connector = _connectors[call.ToolId];

        ToolResult result = await connector
            .ReconcileAsync(call.Id, call.ExternalReference, ct).ConfigureAwait(false);

        return await SettleAsync(call, manifest, result, ct).ConfigureAwait(false);
    }

    public async Task<int> DisableAsync(string toolId, string reason, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tool_manifest SET disabled_reason = @r WHERE tool_id = @id;";
        command.Parameters.AddWithValue("@r", reason);
        command.Parameters.AddWithValue("@id", toolId);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<ToolCall?> GetCallAsync(string callId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = CallSelect + " WHERE id = @id;";
        command.Parameters.AddWithValue("@id", callId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadCall(reader) : null;
    }

    public async Task<IReadOnlyList<ToolCall>> UnknownCallsAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = CallSelect + " WHERE status = @u ORDER BY started_at_utc ASC;";
        command.Parameters.AddWithValue("@u", ToolCallStatus.Unknown);

        var rows = new List<ToolCall>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(ReadCall(reader));
        }

        return rows;
    }

    // ---- internals ----

    /// <summary>
    /// Validates the connector's output before believing any of it (rule 3), then settles the call.
    /// </summary>
    private async Task<ToolCall> SettleAsync(
        ToolCall call, ToolManifest manifest, ToolResult result, CancellationToken ct)
    {
        var endedAt = Iso(_clock.UtcNow);

        if (result.Status != ToolCallStatus.Succeeded)
        {
            ToolCall settled = call with
            {
                Status = result.Status,
                EndedAtUtc = endedAt,
                ExternalReference = result.ExternalReference ?? call.ExternalReference,
                ErrorCode = result.ErrorCode,
            };

            await SaveAsync(settled, ct).ConfigureAwait(false);
            return settled;
        }

        var output = result.StructuredOutputJson ?? "{}";

        if (System.Text.Encoding.UTF8.GetByteCount(output) > MaxOutputBytes)
        {
            return await FailAsync(call, endedAt, "output_too_large", ct).ConfigureAwait(false);
        }

        try
        {
            using JsonDocument schema = JsonDocument.Parse(manifest.OutputSchema);
            using JsonDocument document = JsonDocument.Parse(output);

            SchemaValidationResult validation =
                _validator.Validate(schema.RootElement, document.RootElement);

            if (!validation.IsValid)
            {
                // An external result is untrusted until it passes the schema. A remote side that
                // changed shape is a defect to surface, not a payload to interpret loosely.
                return await FailAsync(call, endedAt, "output_schema_invalid", ct).ConfigureAwait(false);
            }
        }
        catch (JsonException)
        {
            return await FailAsync(call, endedAt, "output_not_json", ct).ConfigureAwait(false);
        }

        ToolCall succeeded = call with
        {
            Status = ToolCallStatus.Succeeded,
            EndedAtUtc = endedAt,
            ExternalReference = result.ExternalReference ?? call.ExternalReference,
            OutputRef = result.Artifacts.FirstOrDefault()?.Ref,
            ErrorCode = null,
        };

        await SaveAsync(succeeded, ct).ConfigureAwait(false);
        return succeeded;
    }

    private async Task<ToolCall> FailAsync(
        ToolCall call, string endedAt, string errorCode, CancellationToken ct)
    {
        ToolCall failed = call with
        {
            Status = ToolCallStatus.Failed,
            EndedAtUtc = endedAt,
            ErrorCode = errorCode,
        };

        await SaveAsync(failed, ct).ConfigureAwait(false);
        return failed;
    }

    /// <summary>Keeps only the fields the manifest says may leave; everything else is a placeholder.</summary>
    private static string Redact(string inputJson, ToolManifest manifest)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(inputJson);
            var redacted = new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                redacted[property.Name] = manifest.DataClassesOut.Contains("PUBLIC", StringComparer.Ordinal)
                    ? property.Value.ToString()
                    : "[redacted]";
            }

            return JsonSerializer.Serialize(redacted);
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    private async Task<int> RecentCallsAsync(string toolId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM tool_call
             WHERE tool_id = @t AND started_at_utc IS NOT NULL AND started_at_utc > @since;
            """;
        command.Parameters.AddWithValue("@t", toolId);
        command.Parameters.AddWithValue("@since", Iso(_clock.UtcNow.AddMinutes(-1)));

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private async Task<(ToolManifest Manifest, string? DisabledReason)> RequireManifestAsync(
        string toolId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tool_id, version, provider, capabilities, input_schema, output_schema, effects,
                   data_classes_in, data_classes_out, auth_mode, timeout_seconds,
                   rate_limit_per_minute, requires_approval, secret_reference_id, disabled_reason
              FROM tool_manifest WHERE tool_id = @id;
            """;
        command.Parameters.AddWithValue("@id", toolId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new ToolException($"No manifest for '{toolId}'.");
        }

        var manifest = new ToolManifest(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), Split(reader.GetString(3)),
            reader.GetString(4), reader.GetString(5), Split(reader.GetString(6)),
            Split(reader.GetString(7)), Split(reader.GetString(8)), reader.GetString(9),
            reader.GetInt32(10), reader.GetInt32(11), reader.GetInt32(12) == 1,
            reader.IsDBNull(13) ? null : reader.GetString(13));

        return (manifest, reader.IsDBNull(14) ? null : reader.GetString(14));
    }

    private async Task<ToolCall> RequireCallAsync(string callId, CancellationToken ct) =>
        await GetCallAsync(callId, ct).ConfigureAwait(false)
        ?? throw new ToolException("Unknown tool call.");

    private Task SaveAsync(ToolCall c, CancellationToken ct) =>
        ExecuteAsync("""
            INSERT INTO tool_call
                (id, work_item_id, task_id, tool_id, capability, input_redacted_json, input_hash,
                 idempotency_key, status, policy_decision_ids, approval_id, started_at_utc,
                 ended_at_utc, external_reference, output_ref, error_code, retry_after_utc)
            VALUES (@id, @w, @task, @tool, @cap, @input, @hash, @idem, @status, @pol, @appr,
                    @start, @end, @ext, @out, @err, @retry)
            ON CONFLICT(id) DO UPDATE SET
                status = excluded.status, policy_decision_ids = excluded.policy_decision_ids,
                approval_id = excluded.approval_id, started_at_utc = excluded.started_at_utc,
                ended_at_utc = excluded.ended_at_utc, external_reference = excluded.external_reference,
                output_ref = excluded.output_ref, error_code = excluded.error_code,
                retry_after_utc = excluded.retry_after_utc;
            """, ct,
            ("@id", c.Id), ("@w", c.WorkItemId), ("@task", (object?)c.TaskId ?? DBNull.Value),
            ("@tool", c.ToolId), ("@cap", c.Capability), ("@input", c.InputRedactedJson),
            ("@hash", c.InputHash), ("@idem", (object?)c.IdempotencyKey ?? DBNull.Value),
            ("@status", c.Status), ("@pol", string.Join(',', c.PolicyDecisionIds)),
            ("@appr", (object?)c.ApprovalId ?? DBNull.Value),
            ("@start", (object?)c.StartedAtUtc ?? DBNull.Value),
            ("@end", (object?)c.EndedAtUtc ?? DBNull.Value),
            ("@ext", (object?)c.ExternalReference ?? DBNull.Value),
            ("@out", (object?)c.OutputRef ?? DBNull.Value),
            ("@err", (object?)c.ErrorCode ?? DBNull.Value),
            ("@retry", (object?)c.RetryAfterUtc ?? DBNull.Value));

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

    private const string CallSelect = """
        SELECT id, work_item_id, task_id, tool_id, capability, input_redacted_json, input_hash,
               idempotency_key, status, policy_decision_ids, approval_id, started_at_utc,
               ended_at_utc, external_reference, output_ref, error_code, retry_after_utc
          FROM tool_call
        """;

    private static ToolCall ReadCall(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
        r.GetString(4), r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
        r.GetString(8), Split(r.GetString(9)), r.IsDBNull(10) ? null : r.GetString(10),
        r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12),
        r.IsDBNull(13) ? null : r.GetString(13), r.IsDBNull(14) ? null : r.GetString(14),
        r.IsDBNull(15) ? null : r.GetString(15), r.IsDBNull(16) ? null : r.GetString(16));

    private static IReadOnlyList<string> Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries);

    private static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
