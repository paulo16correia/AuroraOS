using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Cognition;

/// <summary>How long a frame lives before it is sealed and disposed of (RFC 024 rule 2).</summary>
public sealed record WorkingMemoryOptions(TimeSpan Ttl)
{
    public static readonly WorkingMemoryOptions Default = new(TimeSpan.FromMinutes(30));
}

/// <summary>The temporary, isolated space one cycle works in (RFC 024).</summary>
public sealed class SqliteWorkingMemory : IWorkingMemory
{
    private readonly SqliteConnectionFactory _factory;
    private readonly IClock _clock;
    private readonly WorkingMemoryOptions _options;

    public SqliteWorkingMemory(
        SqliteConnectionFactory factory, IClock clock, WorkingMemoryOptions options)
    {
        _factory = factory;
        _clock = clock;
        _options = options;
    }

    public async Task<WorkingMemoryFrame> OpenAsync(
        string cycleId, string? sessionId, AttentionSet attention, AttentionPolicy policy, CancellationToken ct)
    {
        var frame = new WorkingMemoryFrame(
            Guid.NewGuid().ToString("N"), cycleId, sessionId, WorkingMemoryStatus.Open,
            policy.TokenBudget, policy.MaxItems, policy.SensitivityCeiling,
            Iso(_clock.UtcNow.Add(_options.Ttl)), UsedTokens: 0, UsedItems: 0);

        await ExecuteAsync("""
            INSERT INTO working_memory
                (id, cycle_id, session_id, status, capacity_tokens, capacity_items,
                 sensitivity_ceiling, expires_at_utc)
            VALUES (@id, @c, @s, @status, @tok, @items, @ceiling, @exp);
            """, ct,
            ("@id", frame.Id), ("@c", cycleId), ("@s", (object?)sessionId ?? DBNull.Value),
            ("@status", frame.Status), ("@tok", frame.CapacityTokens), ("@items", frame.CapacityItems),
            ("@ceiling", frame.SensitivityCeiling), ("@exp", frame.ExpiresAtUtc)).ConfigureAwait(false);

        // Seed the frame from what attention actually selected. Nothing else gets in by default —
        // that is the whole point of a bounded context.
        foreach (AttentionItem item in attention.Items)
        {
            await PutAsync(frame.Id, new WorkingItem(
                Guid.NewGuid().ToString("N"), frame.Id, WorkingItemType.Context,
                PayloadJson: null, PayloadRef: item.Ref, SourceRefs: [item.Ref],
                item.Confidence, item.SensitivityClass, item.TokenCost,
                Iso(_clock.UtcNow), item.ExpiresAtUtc, WorkingItemDisposition.Pending), ct)
                .ConfigureAwait(false);
        }

        return (await GetAsync(frame.Id, ct).ConfigureAwait(false))!;
    }

    public async Task<WorkingItem> PutAsync(string workingMemoryId, WorkingItem item, CancellationToken ct)
    {
        WorkingMemoryFrame frame = await GetAsync(workingMemoryId, ct).ConfigureAwait(false)
            ?? throw new WorkingMemoryException("Unknown working memory.");

        if (frame.Status != WorkingMemoryStatus.Open)
        {
            throw new WorkingMemoryException($"The frame is {frame.Status}; nothing more can be added.");
        }

        if (Sensitivity.Rank(item.SensitivityClass) > Sensitivity.Rank(frame.SensitivityCeiling))
        {
            throw new WorkingMemoryException(
                $"{item.SensitivityClass} is above this frame's ceiling of {frame.SensitivityCeiling}.");
        }

        var overItems = frame.UsedItems + 1 > frame.CapacityItems;
        var overTokens = frame.UsedTokens + item.TokenCost > frame.CapacityTokens;

        if (overItems || overTokens)
        {
            // RFC 024 limit case: capacity is exhausted, so either attention drops the least useful
            // item or the cycle asks for clarification. What must never happen is a silent
            // truncation, so this refuses loudly and lets the caller decide.
            throw new WorkingMemoryFullException(
                $"Frame is full ({frame.UsedItems}/{frame.CapacityItems} items, "
                + $"{frame.UsedTokens}/{frame.CapacityTokens} tokens). Drop an item or ask for clarification.");
        }

        WorkingItem stored = item with { WorkingMemoryId = workingMemoryId };

        await ExecuteAsync("""
            INSERT INTO working_item
                (id, working_memory_id, type, payload_json, payload_ref, source_refs, confidence,
                 sensitivity, token_cost, created_at_utc, expires_at_utc, disposition)
            VALUES (@id, @wm, @type, @pj, @pr, @src, @conf, @sens, @tok, @at, @exp, @disp);
            """, ct,
            ("@id", stored.Id), ("@wm", workingMemoryId), ("@type", stored.Type),
            ("@pj", (object?)stored.PayloadJson ?? DBNull.Value),
            ("@pr", (object?)stored.PayloadRef ?? DBNull.Value),
            ("@src", string.Join(',', stored.SourceRefs)), ("@conf", stored.Confidence),
            ("@sens", stored.SensitivityClass), ("@tok", stored.TokenCost),
            ("@at", stored.CreatedAtUtc), ("@exp", (object?)stored.ExpiresAtUtc ?? DBNull.Value),
            ("@disp", stored.Disposition)).ConfigureAwait(false);

        return stored;
    }

    public async Task<WorkingMemoryFrame> SealAsync(string workingMemoryId, CancellationToken ct)
    {
        await ExecuteAsync(
            "UPDATE working_memory SET status = @sealed WHERE id = @id AND status = @open;", ct,
            ("@sealed", WorkingMemoryStatus.Sealed), ("@id", workingMemoryId),
            ("@open", WorkingMemoryStatus.Open)).ConfigureAwait(false);

        return (await GetAsync(workingMemoryId, ct).ConfigureAwait(false))!;
    }

    public async Task<DisposalReport> DisposeFrameAsync(
        string workingMemoryId, IReadOnlyList<ConsolidationDecision> decisions, CancellationToken ct)
    {
        IReadOnlyList<WorkingItem> items = await ItemsAsync(workingMemoryId, ct).ConfigureAwait(false);
        var byId = decisions.ToDictionary(d => d.WorkingItemId, d => d.Disposition, StringComparer.Ordinal);

        var discarded = 0;
        var audited = 0;
        var proposals = new List<ConsolidationProposal>();

        foreach (WorkingItem item in items)
        {
            var disposition = byId.GetValueOrDefault(item.Id, WorkingItemDisposition.Discard);

            switch (disposition)
            {
                case WorkingItemDisposition.Audit:
                    audited++;
                    break;

                case WorkingItemDisposition.Consolidate:
                    // Rule 3: a hypothesis may be offered for consolidation but only ever as a
                    // candidate. It does not become a fact without going through RFC 03.
                    proposals.Add(new ConsolidationProposal(
                        item.Id, item.Type,
                        $"{item.Type.ToLowerInvariant()} from {string.Join(", ", item.SourceRefs)}",
                        MustEnterAsCandidate: item.Type == WorkingItemType.Hypothesis));
                    break;

                default:
                    discarded++;
                    break;
            }

            await ExecuteAsync("UPDATE working_item SET disposition = @d WHERE id = @id;", ct,
                ("@d", disposition), ("@id", item.Id)).ConfigureAwait(false);
        }

        await ExecuteAsync("UPDATE working_memory SET status = @s WHERE id = @id;", ct,
            ("@s", WorkingMemoryStatus.Discarded), ("@id", workingMemoryId)).ConfigureAwait(false);

        // Rule 4: an approved operational summary, never the drafts themselves dressed up as
        // "internal reasoning".
        var summary = $"{items.Count} item(s): {discarded} discarded, {audited} audited, "
                    + $"{proposals.Count} proposed for consolidation.";

        return new DisposalReport(workingMemoryId, discarded, audited, proposals, summary);
    }

    public async Task<int> ExpireDueAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE working_memory SET status = @expired
             WHERE status = @open AND expires_at_utc <= @now;
            """;
        command.Parameters.AddWithValue("@expired", WorkingMemoryStatus.Expired);
        command.Parameters.AddWithValue("@open", WorkingMemoryStatus.Open);
        command.Parameters.AddWithValue("@now", Iso(_clock.UtcNow));

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<WorkingItem> TransferAsync(
        string itemId, string toWorkingMemoryId, string reason, CancellationToken ct)
    {
        WorkingMemoryFrame target = await GetAsync(toWorkingMemoryId, ct).ConfigureAwait(false)
            ?? throw new WorkingMemoryException("Unknown target frame.");

        if (target.Status != WorkingMemoryStatus.Open)
        {
            throw new WorkingMemoryException("The target frame is not open.");
        }

        // Rule 1: frames do not share by default. A transfer is an explicit act with a reason,
        // which is what keeps one cycle's context out of another's by accident.
        await ExecuteAsync(
            "UPDATE working_item SET working_memory_id = @to WHERE id = @id;", ct,
            ("@to", toWorkingMemoryId), ("@id", itemId)).ConfigureAwait(false);

        IReadOnlyList<WorkingItem> items = await ItemsAsync(toWorkingMemoryId, ct).ConfigureAwait(false);
        return items.First(i => i.Id == itemId);
    }

    public async Task<WorkingMemoryFrame?> GetAsync(string workingMemoryId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT w.id, w.cycle_id, w.session_id, w.status, w.capacity_tokens, w.capacity_items,
                   w.sensitivity_ceiling, w.expires_at_utc,
                   COALESCE(SUM(i.token_cost), 0), COUNT(i.id)
              FROM working_memory w
              LEFT JOIN working_item i ON i.working_memory_id = w.id
             WHERE w.id = @id
             GROUP BY w.id;
            """;
        command.Parameters.AddWithValue("@id", workingMemoryId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new WorkingMemoryFrame(
            reader.GetString(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
            reader.GetInt32(4), reader.GetInt32(5), reader.GetString(6), reader.GetString(7),
            reader.GetInt32(8), reader.GetInt32(9));
    }

    public async Task<IReadOnlyList<WorkingItem>> ItemsAsync(string workingMemoryId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, working_memory_id, type, payload_json, payload_ref, source_refs, confidence,
                   sensitivity, token_cost, created_at_utc, expires_at_utc, disposition
              FROM working_item WHERE working_memory_id = @id ORDER BY created_at_utc ASC, rowid ASC;
            """;
        command.Parameters.AddWithValue("@id", workingMemoryId);

        var rows = new List<WorkingItem>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new WorkingItem(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5).Split(',', StringSplitOptions.RemoveEmptyEntries),
                reader.GetDouble(6), reader.GetString(7), reader.GetInt32(8),
                reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetString(11)));
        }

        return rows;
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

    private static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
