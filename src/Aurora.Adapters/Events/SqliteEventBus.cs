using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Events;

/// <summary>
/// SQLite-backed Event Bus (RFC 050): transactional outbox, idempotent fan-out, retry with an
/// auditable dead-letter queue, and replay from a cursor.
/// </summary>
/// <remarks>
/// In-process and single-node, which is what the frozen implementation order asks for at step 3.
/// The properties that matter are structural rather than distributed: an event cannot outlive a
/// rolled-back state change, fan-out can be repeated after a crash without duplicating a delivery,
/// and a consumer that keeps failing ends up somewhere an operator can see rather than nowhere.
/// </remarks>
public sealed class SqliteEventBus : IEventBus
{
    private readonly SqliteConnectionFactory _factory;
    private readonly IOutbox _outbox;
    private readonly IClock _clock;

    public SqliteEventBus(SqliteConnectionFactory factory, IOutbox outbox, IClock clock)
    {
        _factory = factory;
        _outbox = outbox;
        _clock = clock;
    }

    public async Task<IDbTransactionScope> BeginAsync(CancellationToken ct)
    {
        SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        return new SqliteTransactionScope(connection, transaction);
    }

    public async Task<DomainEvent> PublishAsync(OutboxWrite write, CancellationToken ct)
    {
        await using IDbTransactionScope scope = await BeginAsync(ct).ConfigureAwait(false);
        DomainEvent published = await _outbox.EnqueueAsync(write, scope, ct).ConfigureAwait(false);
        await scope.CommitAsync(ct).ConfigureAwait(false);
        return published;
    }

    public async Task<Subscription> SubscribeAsync(Subscription subscription, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO subscription
                (id, consumer, event_types, filter_ref, mode, checkpoint, status,
                 max_attempts, max_schema_version, diagnosis)
            VALUES (@id, @consumer, @types, @filter, @mode, @cp, @status, @max, @maxver, @diag)
            ON CONFLICT(id) DO UPDATE SET
                consumer = excluded.consumer,
                event_types = excluded.event_types,
                filter_ref = excluded.filter_ref,
                mode = excluded.mode,
                max_attempts = excluded.max_attempts,
                max_schema_version = excluded.max_schema_version,
                -- A pause is lifted exactly when its cause is gone: the consumer now declares a
                -- schema version it did not understand before. Any other re-registration leaves
                -- the status alone, so re-subscribing can never quietly clear a FAILED state.
                status = CASE
                    WHEN subscription.status = @paused
                     AND excluded.max_schema_version > subscription.max_schema_version
                    THEN @active ELSE subscription.status END,
                diagnosis = CASE
                    WHEN subscription.status = @paused
                     AND excluded.max_schema_version > subscription.max_schema_version
                    THEN NULL ELSE subscription.diagnosis END;
            """;
        command.Parameters.AddWithValue("@id", subscription.Id);
        command.Parameters.AddWithValue("@consumer", subscription.Consumer);
        command.Parameters.AddWithValue("@types", string.Join(',', subscription.EventTypes));
        command.Parameters.AddWithValue("@filter", (object?)subscription.FilterRef ?? DBNull.Value);
        command.Parameters.AddWithValue("@mode", subscription.Mode);
        command.Parameters.AddWithValue("@cp", subscription.Checkpoint);
        command.Parameters.AddWithValue("@status", subscription.Status);
        command.Parameters.AddWithValue("@max", subscription.MaxAttempts);
        command.Parameters.AddWithValue("@maxver", subscription.MaxSchemaVersion);
        command.Parameters.AddWithValue("@diag", (object?)subscription.Diagnosis ?? DBNull.Value);
        command.Parameters.AddWithValue("@paused", SubscriptionStatus.Paused);
        command.Parameters.AddWithValue("@active", SubscriptionStatus.Active);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return (await LoadAsync(subscription.Id, ct).ConfigureAwait(false))!;
    }

    public async Task<int> PumpAsync(IEventConsumer consumer, CancellationToken ct)
    {
        var processed = 0;

        foreach (var subscriptionId in await ActiveSubscriptionIdsAsync(consumer.Name, ct).ConfigureAwait(false))
        {
            Subscription? subscription = await LoadAsync(subscriptionId, ct).ConfigureAwait(false);
            if (subscription is null || subscription.Status != SubscriptionStatus.Active)
            {
                continue;
            }

            var checkpoint = subscription.Checkpoint;

            foreach ((long sequence, DomainEvent domainEvent) in
                     await PendingAsync(subscription, ct).ConfigureAwait(false))
            {
                // A consumer that does not understand the schema pauses with a diagnosis. It must
                // never interpret unknown fields permissively, so the checkpoint stays put and the
                // event is waiting when the consumer is upgraded.
                if (domainEvent.SchemaVersion > subscription.MaxSchemaVersion)
                {
                    await PauseAsync(
                        subscription.Id,
                        $"Event {domainEvent.Type} is schema v{domainEvent.SchemaVersion}; "
                        + $"this consumer understands up to v{subscription.MaxSchemaVersion}.",
                        ct).ConfigureAwait(false);
                    break;
                }

                Delivery delivery = await EnsureDeliveryAsync(domainEvent.EventId, subscription.Id, ct)
                    .ConfigureAwait(false);

                if (delivery.Status is DeliveryStatus.Acked or DeliveryStatus.Skipped or DeliveryStatus.DeadLetter)
                {
                    checkpoint = sequence;
                    continue;
                }

                ConsumeResult result;
                try
                {
                    result = await consumer.ConsumeAsync(domainEvent, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = new ConsumeResult(ConsumeOutcome.Retry, ex.GetType().Name);
                }

                processed++;

                if (result.Outcome is ConsumeOutcome.Acked or ConsumeOutcome.Skipped)
                {
                    await SettleAsync(
                        delivery.DeliveryId,
                        result.Outcome == ConsumeOutcome.Acked ? DeliveryStatus.Acked : DeliveryStatus.Skipped,
                        delivery.Attempt + 1, result.Error, ct).ConfigureAwait(false);
                    checkpoint = sequence;
                    continue;
                }

                var attempt = delivery.Attempt + 1;
                if (attempt >= subscription.MaxAttempts)
                {
                    // Rule 4: nothing is silently discarded. It lands somewhere auditable and the
                    // subscription moves on rather than wedging behind one poisonous event.
                    await SettleAsync(
                        delivery.DeliveryId, DeliveryStatus.DeadLetter, attempt, result.Error, ct)
                        .ConfigureAwait(false);
                    checkpoint = sequence;
                    continue;
                }

                await SettleAsync(delivery.DeliveryId, DeliveryStatus.Retrying, attempt, result.Error, ct)
                    .ConfigureAwait(false);

                // Stop here: advancing past an unsettled event would reorder this subscription's
                // stream, and the retry is meant to happen before anything later is seen.
                break;
            }

            if (checkpoint != subscription.Checkpoint)
            {
                await SaveCheckpointAsync(subscription.Id, checkpoint, ct).ConfigureAwait(false);
            }
        }

        return processed;
    }

    public async Task<Delivery?> AckAsync(string deliveryId, CancellationToken ct)
    {
        await SettleAsync(deliveryId, DeliveryStatus.Acked, attempt: null, error: null, ct).ConfigureAwait(false);
        return await LoadDeliveryAsync(deliveryId, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Delivery>> ReplayAsync(string subscriptionId, long cursor, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.delivery_id, d.event_id, d.subscription_id, d.attempt, d.delivered_at_utc,
                   d.status, d.last_error
              FROM delivery d
              JOIN domain_event e ON e.event_id = d.event_id
             WHERE d.subscription_id = @sub AND e.sequence > @cursor
             ORDER BY e.sequence ASC;
            """;
        command.Parameters.AddWithValue("@sub", subscriptionId);
        command.Parameters.AddWithValue("@cursor", cursor);

        return await ReadDeliveriesAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Delivery>> DeadLettersAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT delivery_id, event_id, subscription_id, attempt, delivered_at_utc, status, last_error
              FROM delivery WHERE status = @dead ORDER BY delivered_at_utc ASC;
            """;
        command.Parameters.AddWithValue("@dead", DeliveryStatus.DeadLetter);

        return await ReadDeliveriesAsync(command, ct).ConfigureAwait(false);
    }

    // ---- internals ----

    private async Task<IReadOnlyList<string>> ActiveSubscriptionIdsAsync(string consumer, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM subscription WHERE consumer = @c AND status = @a ORDER BY id;";
        command.Parameters.AddWithValue("@c", consumer);
        command.Parameters.AddWithValue("@a", SubscriptionStatus.Active);

        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private async Task<Subscription?> LoadAsync(string id, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, consumer, event_types, filter_ref, mode, checkpoint, status,
                   max_attempts, max_schema_version, diagnosis
              FROM subscription WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@id", id);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new Subscription(
            reader.GetString(0), reader.GetString(1),
            reader.GetString(2).Split(',', StringSplitOptions.RemoveEmptyEntries),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4), reader.GetInt64(5), reader.GetString(6),
            reader.GetInt32(7), reader.GetInt32(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));
    }

    private async Task<IReadOnlyList<(long Sequence, DomainEvent Event)>> PendingAsync(
        Subscription subscription, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var placeholders = string.Join(',', subscription.EventTypes.Select((_, i) => "@t" + i));
        command.CommandText = $"""
            SELECT sequence, event_id, type, schema_version, producer, occurred_at_utc, correlation_id,
                   causation_id, aggregate_ref, payload_json, payload_ref, sensitivity,
                   idempotency_key, integrity_hash
              FROM domain_event
             WHERE sequence > @cp AND type IN ({placeholders})
             ORDER BY sequence ASC;
            """;
        command.Parameters.AddWithValue("@cp", subscription.Checkpoint);
        for (var i = 0; i < subscription.EventTypes.Count; i++)
        {
            command.Parameters.AddWithValue("@t" + i, subscription.EventTypes[i]);
        }

        var events = new List<(long, DomainEvent)>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            events.Add((reader.GetInt64(0), new DomainEvent(
                reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4),
                reader.GetString(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.GetString(13))));
        }

        return events;
    }

    private async Task<Delivery> EnsureDeliveryAsync(string eventId, string subscriptionId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using (var insert = connection.CreateCommand())
        {
            // OR IGNORE against the unique index is what makes fan-out re-executable.
            insert.CommandText = """
                INSERT OR IGNORE INTO delivery (delivery_id, event_id, subscription_id, attempt, status)
                VALUES (@id, @e, @s, 0, @pending);
                """;
            insert.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("@e", eventId);
            insert.Parameters.AddWithValue("@s", subscriptionId);
            insert.Parameters.AddWithValue("@pending", DeliveryStatus.Pending);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT delivery_id, event_id, subscription_id, attempt, delivered_at_utc, status, last_error
              FROM delivery WHERE event_id = @e AND subscription_id = @s;
            """;
        select.Parameters.AddWithValue("@e", eventId);
        select.Parameters.AddWithValue("@s", subscriptionId);

        return (await ReadDeliveriesAsync(select, ct).ConfigureAwait(false))[0];
    }

    private async Task SettleAsync(string deliveryId, string status, int? attempt, string? error, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE delivery
               SET status = @s,
                   attempt = COALESCE(@a, attempt),
                   delivered_at_utc = @at,
                   last_error = @err
             WHERE delivery_id = @id;
            """;
        command.Parameters.AddWithValue("@s", status);
        command.Parameters.AddWithValue("@a", (object?)attempt ?? DBNull.Value);
        command.Parameters.AddWithValue("@at", _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@err", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("@id", deliveryId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task PauseAsync(string subscriptionId, string diagnosis, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE subscription SET status = @p, diagnosis = @d WHERE id = @id;";
        command.Parameters.AddWithValue("@p", SubscriptionStatus.Paused);
        command.Parameters.AddWithValue("@d", diagnosis);
        command.Parameters.AddWithValue("@id", subscriptionId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task SaveCheckpointAsync(string subscriptionId, long checkpoint, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE subscription SET checkpoint = @cp WHERE id = @id;";
        command.Parameters.AddWithValue("@cp", checkpoint);
        command.Parameters.AddWithValue("@id", subscriptionId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<Delivery?> LoadDeliveryAsync(string deliveryId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT delivery_id, event_id, subscription_id, attempt, delivered_at_utc, status, last_error
              FROM delivery WHERE delivery_id = @id;
            """;
        command.Parameters.AddWithValue("@id", deliveryId);

        IReadOnlyList<Delivery> rows = await ReadDeliveriesAsync(command, ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    private static async Task<IReadOnlyList<Delivery>> ReadDeliveriesAsync(SqliteCommand command, CancellationToken ct)
    {
        var rows = new List<Delivery>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new Delivery(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return rows;
    }
}
