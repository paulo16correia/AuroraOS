using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Cognition;

/// <summary>Bounded, explained selection of what a cycle will process (RFC 023).</summary>
public sealed class SqliteAttentionSystem : IAttentionSystem
{
    private readonly SqliteConnectionFactory _factory;
    private readonly IAttentionAuthorization _authorization;
    private readonly IClock _clock;

    public SqliteAttentionSystem(
        SqliteConnectionFactory factory, IAttentionAuthorization authorization, IClock clock)
    {
        _factory = factory;
        _authorization = authorization;
        _clock = clock;
    }

    public async Task<AttentionSet> RankAsync(
        string cycleId,
        IReadOnlyList<AttentionItem> candidates,
        AttentionPolicy policy,
        MemoryAccessContext access,
        CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var selected = new List<AttentionItem>();
        var excluded = new List<AttentionItem>();
        var scored = new List<AttentionItem>();

        foreach (AttentionItem candidate in candidates)
        {
            // Rule 1: authorisation comes first, before any relevance is computed. Rule 4 follows
            // from that ordering — urgency has nothing to bid with once the item is already gone.
            if (!_authorization.MayConsider(candidate, access))
            {
                excluded.Add(candidate with { Score = 0, ReasonCodes = [AttentionReason.NotAuthorised] });
                continue;
            }

            if (Sensitivity.Rank(candidate.SensitivityClass) > Sensitivity.Rank(policy.SensitivityCeiling))
            {
                excluded.Add(candidate with
                {
                    Score = 0, ReasonCodes = [AttentionReason.AboveSensitivityCeiling],
                });
                continue;
            }

            if (candidate.ExpiresAtUtc is { } expires
                && DateTimeOffset.TryParse(
                    expires, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiry)
                && expiry <= now)
            {
                excluded.Add(candidate with { Score = 0, ReasonCodes = [AttentionReason.Expired] });
                continue;
            }

            scored.Add(candidate with { Score = Score(candidate, policy) });
        }

        // Deterministic ordering: score first, then the tie-breakers RFC 023 names — more recent,
        // better-confirmed evidence wins, and the reference breaks a remaining tie so two identical
        // runs never disagree.
        foreach (AttentionItem candidate in scored
                     .OrderByDescending(c => c.Score)
                     .ThenByDescending(c => c.Recency)
                     .ThenByDescending(c => c.Confidence)
                     .ThenBy(c => c.Ref, StringComparer.Ordinal))
        {
            if (candidate.Score < policy.SelectionThreshold)
            {
                excluded.Add(candidate with { ReasonCodes = [AttentionReason.BelowThreshold] });
                continue;
            }

            if (selected.Count >= policy.MaxItems)
            {
                excluded.Add(candidate with { ReasonCodes = [AttentionReason.ItemLimitReached] });
                continue;
            }

            if (selected.Sum(i => i.TokenCost) + candidate.TokenCost > policy.TokenBudget)
            {
                excluded.Add(candidate with { ReasonCodes = [AttentionReason.BudgetExhausted] });
                continue;
            }

            selected.Add(candidate with { ReasonCodes = [AttentionReason.Selected] });
        }

        var set = new AttentionSet(
            Guid.NewGuid().ToString("N"), cycleId, selected, excluded,
            policy.TokenBudget, policy.MaxItems, AttentionSetStatus.Proposed,
            now.ToString("O", CultureInfo.InvariantCulture));

        await SaveAsync(set, ct).ConfigureAwait(false);
        return set;
    }

    public async Task<AttentionSet> FocusAsync(string cycleId, string itemRef, CancellationToken ct)
    {
        AttentionSet set = await GetAsync(cycleId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No attention set for this cycle.");

        if (set.Items.All(i => i.Ref != itemRef))
        {
            throw new InvalidOperationException("That item is not in the selected set.");
        }

        await ExecuteAsync("UPDATE attention_set SET status = @s WHERE cycle_id = @c;", ct,
            ("@s", AttentionSetStatus.Locked), ("@c", cycleId)).ConfigureAwait(false);

        return set with { Status = AttentionSetStatus.Locked };
    }

    public Task ReleaseAsync(string cycleId, CancellationToken ct) =>
        ExecuteAsync("UPDATE attention_set SET status = @s WHERE cycle_id = @c;", ct,
            ("@s", AttentionSetStatus.Released), ("@c", cycleId));

    public async Task<AttentionSet?> GetAsync(string cycleId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);

        string setId;
        int budget, limit;
        string status, selectedAt;
        await using (var head = connection.CreateCommand())
        {
            head.CommandText =
                "SELECT id, token_budget, item_limit, status, selected_at_utc FROM attention_set WHERE cycle_id = @c;";
            head.Parameters.AddWithValue("@c", cycleId);
            await using var reader = await head.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            setId = reader.GetString(0);
            budget = reader.GetInt32(1);
            limit = reader.GetInt32(2);
            status = reader.GetString(3);
            selectedAt = reader.GetString(4);
        }

        var selected = new List<AttentionItem>();
        var excluded = new List<AttentionItem>();

        await using (var items = connection.CreateCommand())
        {
            items.CommandText = """
                SELECT ref, kind, relevance, urgency, novelty, impact, confidence, recency,
                       sensitivity, token_cost, expires_at_utc, score, reason_codes, selected
                  FROM attention_item WHERE set_id = @s ORDER BY score DESC;
                """;
            items.Parameters.AddWithValue("@s", setId);

            await using var reader = await items.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var item = new AttentionItem(
                    reader.GetString(0), reader.GetString(1), reader.GetDouble(2), reader.GetDouble(3),
                    reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6), reader.GetDouble(7),
                    reader.GetString(8), reader.GetInt32(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetDouble(11),
                    reader.GetString(12).Split(',', StringSplitOptions.RemoveEmptyEntries));

                (reader.GetInt32(13) == 1 ? selected : excluded).Add(item);
            }
        }

        return new AttentionSet(setId, cycleId, selected, excluded, budget, limit, status, selectedAt);
    }

    private static double Score(AttentionItem c, AttentionPolicy p)
    {
        var total = p.RelevanceWeight + p.UrgencyWeight + p.NoveltyWeight
                  + p.ImpactWeight + p.ConfidenceWeight + p.RecencyWeight;

        var weighted = (c.Relevance * p.RelevanceWeight)
                     + (c.Urgency * p.UrgencyWeight)
                     + (c.Novelty * p.NoveltyWeight)
                     + (c.Impact * p.ImpactWeight)
                     + (c.Confidence * p.ConfidenceWeight)
                     + (c.Recency * p.RecencyWeight);

        return total <= 0 ? 0 : weighted / total;
    }

    private async Task SaveAsync(AttentionSet set, CancellationToken ct)
    {
        await ExecuteAsync("""
            INSERT INTO attention_set (id, cycle_id, token_budget, item_limit, status, selected_at_utc)
            VALUES (@id, @c, @b, @l, @s, @at)
            ON CONFLICT(cycle_id) DO UPDATE SET
                id = excluded.id, token_budget = excluded.token_budget,
                item_limit = excluded.item_limit, status = excluded.status,
                selected_at_utc = excluded.selected_at_utc;
            """, ct,
            ("@id", set.Id), ("@c", set.CycleId), ("@b", set.TokenBudget),
            ("@l", set.ItemLimit), ("@s", set.Status), ("@at", set.SelectedAtUtc)).ConfigureAwait(false);

        foreach ((AttentionItem item, var isSelected) in
                 set.Items.Select(i => (i, true)).Concat(set.Excluded.Select(i => (i, false))))
        {
            await ExecuteAsync("""
                INSERT INTO attention_item
                    (id, set_id, ref, kind, relevance, urgency, novelty, impact, confidence, recency,
                     sensitivity, token_cost, expires_at_utc, score, reason_codes, selected)
                VALUES (@id, @set, @ref, @kind, @rel, @urg, @nov, @imp, @conf, @rec,
                        @sens, @tok, @exp, @score, @codes, @sel);
                """, ct,
                ("@id", Guid.NewGuid().ToString("N")), ("@set", set.Id), ("@ref", item.Ref),
                ("@kind", item.Kind), ("@rel", item.Relevance), ("@urg", item.Urgency),
                ("@nov", item.Novelty), ("@imp", item.Impact), ("@conf", item.Confidence),
                ("@rec", item.Recency), ("@sens", item.SensitivityClass), ("@tok", item.TokenCost),
                ("@exp", (object?)item.ExpiresAtUtc ?? DBNull.Value), ("@score", item.Score),
                ("@codes", string.Join(',', item.ReasonCodes ?? [])),
                ("@sel", isSelected ? 1 : 0)).ConfigureAwait(false);
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
}
