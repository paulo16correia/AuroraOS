using System.Globalization;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Cryptography;

namespace Aurora.Adapters.Events;

/// <summary>
/// Transactional outbox (RFC 050 rule 1). The event is written inside the producer's own
/// transaction, so it commits with the state change it describes or not at all.
/// </summary>
public sealed class SqliteOutbox : IOutbox
{
    /// <summary>ASCII Unit Separator delimiting the integrity pre-image fields.</summary>
    private const char UnitSeparator = (char)0x1F;

    private readonly IEventCatalogue _catalogue;
    private readonly IClock _clock;

    public SqliteOutbox(IEventCatalogue catalogue, IClock clock)
    {
        _catalogue = catalogue;
        _clock = clock;
    }

    public async Task<DomainEvent> EnqueueAsync(OutboxWrite write, IDbTransactionScope scope, CancellationToken ct)
    {
        if (scope is not SqliteTransactionScope sqlite)
        {
            throw new EventContractException("The outbox requires a scope opened by the same bus.");
        }

        Validate(write);

        var occurredAt = _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var domainEvent = new DomainEvent(
            Guid.NewGuid().ToString("N"),
            write.Type,
            write.SchemaVersion,
            write.Producer,
            occurredAt,
            write.CorrelationId,
            write.CausationId,
            write.AggregateRef,
            write.PayloadJson,
            write.PayloadRef,
            write.SensitivityClass,
            write.IdempotencyKey,
            IntegrityHash: string.Empty);

        domainEvent = domainEvent with { IntegrityHash = Hash(domainEvent) };

        await using var command = sqlite.Connection.CreateCommand();
        command.Transaction = sqlite.Transaction;
        command.CommandText = """
            INSERT INTO domain_event
                (event_id, type, schema_version, producer, occurred_at_utc, correlation_id,
                 causation_id, aggregate_ref, payload_json, payload_ref, sensitivity,
                 idempotency_key, integrity_hash, tenant_id)
            VALUES (@id, @type, @ver, @prod, @at, @corr, @caus, @agg, @pj, @pr, @sens, @idem,
                    @hash, @tenant);
            """;
        command.Parameters.AddWithValue("@id", domainEvent.EventId);
        command.Parameters.AddWithValue("@type", domainEvent.Type);
        command.Parameters.AddWithValue("@ver", domainEvent.SchemaVersion);
        command.Parameters.AddWithValue("@prod", domainEvent.Producer);
        command.Parameters.AddWithValue("@at", domainEvent.OccurredAtUtc);
        command.Parameters.AddWithValue("@corr", domainEvent.CorrelationId);
        command.Parameters.AddWithValue("@caus", (object?)domainEvent.CausationId ?? DBNull.Value);
        command.Parameters.AddWithValue("@agg", (object?)domainEvent.AggregateRef ?? DBNull.Value);
        command.Parameters.AddWithValue("@pj", (object?)domainEvent.PayloadJson ?? DBNull.Value);
        command.Parameters.AddWithValue("@pr", (object?)domainEvent.PayloadRef ?? DBNull.Value);
        command.Parameters.AddWithValue("@sens", domainEvent.SensitivityClass);
        command.Parameters.AddWithValue("@idem", (object?)domainEvent.IdempotencyKey ?? DBNull.Value);
        command.Parameters.AddWithValue("@hash", domainEvent.IntegrityHash);
        command.Parameters.AddWithValue("@tenant", write.TenantId);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return domainEvent;
    }

    /// <summary>Enforces the publication rules that RFC 050 states as mandatory.</summary>
    private void Validate(OutboxWrite write)
    {
        if (string.IsNullOrWhiteSpace(write.Type))
        {
            throw new EventContractException("An event must declare a type.");
        }

        if (string.IsNullOrWhiteSpace(write.CorrelationId))
        {
            throw new EventContractException("An event must carry a correlation id (LAW-007).");
        }

        if (!Sensitivity.IsKnown(write.SensitivityClass))
        {
            throw new EventContractException($"Unknown sensitivity '{write.SensitivityClass}'.");
        }

        // LAW-005: state that crosses a boundary says who owns it. One tenant today, and an event
        // that named another would be state this instance has no business carrying.
        if (!Tenant.IsKnown(write.TenantId))
        {
            throw new EventContractException($"'{write.TenantId}' is not this instance's tenant.");
        }

        // LAW-007's verifiable control: each producer declares its events and their schema version.
        // Checked here rather than documented, so an undeclared event cannot be published at all —
        // which is what stops the bus becoming a place where anything can be asserted about
        // anything, including by a caller reaching the ingress endpoint from outside.
        if (!_catalogue.TryValidate(write, out var violation))
        {
            throw new EventContractException($"{violation} (LAW-007).");
        }

        var hasJson = !string.IsNullOrEmpty(write.PayloadJson);
        var hasRef = !string.IsNullOrEmpty(write.PayloadRef);

        if (hasJson == hasRef)
        {
            throw new EventContractException("An event carries exactly one of payload_json or payload_ref.");
        }

        // Rule 3: big or sensitive data travels as an authoritative reference. The bus carries
        // facts, not the sensitive content itself, and never replaces canonical storage.
        if (Sensitivity.RequiresReference(write.SensitivityClass) && hasJson)
        {
            throw new EventContractException(
                $"A {write.SensitivityClass} event must use payload_ref, not an open payload.");
        }
    }

    private static string Hash(DomainEvent e) => Hashing.Sha256Hex(string.Join(
        UnitSeparator,
        new[]
        {
            e.EventId, e.Type, e.SchemaVersion.ToString(CultureInfo.InvariantCulture), e.Producer,
            e.OccurredAtUtc, e.CorrelationId, e.CausationId ?? string.Empty,
            e.AggregateRef ?? string.Empty, e.PayloadJson ?? string.Empty, e.PayloadRef ?? string.Empty,
            e.SensitivityClass, e.IdempotencyKey ?? string.Empty,
        }));
}
