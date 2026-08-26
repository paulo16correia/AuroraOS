using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.WorkItems;

/// <summary>
/// Units of work over SQLite (RFC 02).
/// </summary>
/// <remarks>
/// Rule 1 — at most one active work item per idempotency key — is enforced by looking for an active
/// one before inserting, inside the same connection. A partial unique index would be tidier, but it
/// would turn the second arrival into an exception at the storage layer, and the right answer to a
/// repeated request is the work already in flight rather than an error.
/// </remarks>
public sealed class SqliteWorkItemService : IWorkItemService
{
    /// <summary>Which statuses may follow which. Terminal ones have nowhere to go.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> Allowed =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [WorkItemStatus.Received] =
                [WorkItemStatus.Contextualized, WorkItemStatus.Failed, WorkItemStatus.Cancelled],
            [WorkItemStatus.Contextualized] =
                [WorkItemStatus.Deliberating, WorkItemStatus.Failed, WorkItemStatus.Cancelled],
            [WorkItemStatus.Deliberating] =
                [WorkItemStatus.WaitingApproval, WorkItemStatus.Executing, WorkItemStatus.Completed,
                 WorkItemStatus.Failed, WorkItemStatus.Cancelled],
            [WorkItemStatus.WaitingApproval] =
                [WorkItemStatus.Executing, WorkItemStatus.Completed, WorkItemStatus.Failed,
                 WorkItemStatus.Cancelled],
            [WorkItemStatus.Executing] =
                [WorkItemStatus.Completed, WorkItemStatus.Failed, WorkItemStatus.Cancelled],
            [WorkItemStatus.Completed] = [],
            [WorkItemStatus.Failed] = [],
            [WorkItemStatus.Cancelled] = [],
        };

    private readonly SqliteConnectionFactory _factory;
    private readonly IClock _clock;

    public SqliteWorkItemService(SqliteConnectionFactory factory, IClock clock)
    {
        _factory = factory;
        _clock = clock;
    }

    public async Task<WorkItem> HandleAsync(
        string correlationId, string idempotencyKey, string? causationId, string? eventId,
        string? deadlineAtUtc, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            // Without a key rule 1 has nothing to count, and every repeat becomes a new unit of
            // work that looks exactly like the first.
            throw new WorkItemException("A work item needs an idempotency key.");
        }

        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);

        await using (SqliteCommand existing = connection.CreateCommand())
        {
            existing.CommandText =
                Select + " WHERE idempotency_key = @k ORDER BY created_at_utc DESC, rowid DESC;";

            existing.Parameters.AddWithValue("@k", idempotencyKey);

            await using SqliteDataReader reader =
                await existing.ExecuteReaderAsync(ct).ConfigureAwait(false);

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                WorkItem candidate = Read(reader);

                if (WorkItemStatus.IsActive(candidate.Status))
                {
                    // The same request arriving twice joins the work in flight. Not an error, and
                    // not a second unit of work beside the first.
                    return candidate;
                }
            }
        }

        var now = Iso(_clock.UtcNow);
        var item = new WorkItem(
            Guid.NewGuid().ToString("N"), correlationId, causationId, eventId,
            WorkItemStatus.Received, deadlineAtUtc, RetryCount: 0, idempotencyKey, now, now);

        await using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO work_item
                (id, correlation_id, causation_id, event_id, status, deadline_at_utc, retry_count,
                 idempotency_key, created_at_utc, updated_at_utc, cancelled_by)
            VALUES (@id, @corr, @cause, @event, @status, @deadline, 0, @key, @c, @u, NULL);
            """;

        insert.Parameters.AddWithValue("@id", item.Id);
        insert.Parameters.AddWithValue("@corr", item.CorrelationId);
        insert.Parameters.AddWithValue("@cause", (object?)item.CausationId ?? DBNull.Value);
        insert.Parameters.AddWithValue("@event", (object?)item.EventId ?? DBNull.Value);
        insert.Parameters.AddWithValue("@status", item.Status);
        insert.Parameters.AddWithValue("@deadline", (object?)item.DeadlineAtUtc ?? DBNull.Value);
        insert.Parameters.AddWithValue("@key", item.IdempotencyKey);
        insert.Parameters.AddWithValue("@c", now);
        insert.Parameters.AddWithValue("@u", now);

        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return item;
    }

    public async Task<WorkItem> AdvanceAsync(string workItemId, string status, CancellationToken ct)
    {
        WorkItem item = await RequireAsync(workItemId, ct).ConfigureAwait(false);

        if (!Allowed.TryGetValue(item.Status, out var next) || !next.Contains(status, StringComparer.Ordinal))
        {
            throw new WorkItemException(
                $"A work item does not go from {item.Status} to {status}.");
        }

        return await WriteAsync(item with { Status = status }, ct).ConfigureAwait(false);
    }

    public async Task<WorkItem> CancelAsync(string workItemId, string actor, CancellationToken ct)
    {
        WorkItem item = await RequireAsync(workItemId, ct).ConfigureAwait(false);

        if (!WorkItemStatus.IsActive(item.Status))
        {
            throw new WorkItemException($"This work item is already {item.Status}.");
        }

        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new WorkItemException("Cancelling names who cancelled.");
        }

        return await WriteAsync(
            item with { Status = WorkItemStatus.Cancelled, CancelledBy = actor }, ct)
            .ConfigureAwait(false);
    }

    public async Task<WorkItem?> GetAsync(string workItemId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = Select + " WHERE id = @id;";
        command.Parameters.AddWithValue("@id", workItemId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<WorkItem>> ActiveAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = Select + " ORDER BY created_at_utc ASC, rowid ASC;";

        var items = new List<WorkItem>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            WorkItem item = Read(reader);
            if (WorkItemStatus.IsActive(item.Status))
            {
                items.Add(item);
            }
        }

        return items;
    }

    private async Task<WorkItem> RequireAsync(string id, CancellationToken ct) =>
        await GetAsync(id, ct).ConfigureAwait(false)
        ?? throw new WorkItemException("Unknown work item.");

    private async Task<WorkItem> WriteAsync(WorkItem item, CancellationToken ct)
    {
        WorkItem updated = item with { UpdatedAtUtc = Iso(_clock.UtcNow) };

        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE work_item SET status = @s, updated_at_utc = @u, cancelled_by = @by, " +
            "retry_count = @r WHERE id = @id;";

        command.Parameters.AddWithValue("@s", updated.Status);
        command.Parameters.AddWithValue("@u", updated.UpdatedAtUtc);
        command.Parameters.AddWithValue("@by", (object?)updated.CancelledBy ?? DBNull.Value);
        command.Parameters.AddWithValue("@r", updated.RetryCount);
        command.Parameters.AddWithValue("@id", updated.Id);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return updated;
    }

    private const string Select = """
        SELECT id, correlation_id, causation_id, event_id, status, deadline_at_utc, retry_count,
               idempotency_key, created_at_utc, updated_at_utc, cancelled_by
          FROM work_item
        """;

    private static WorkItem Read(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetInt32(6), reader.GetString(7), reader.GetString(8), reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10));

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
