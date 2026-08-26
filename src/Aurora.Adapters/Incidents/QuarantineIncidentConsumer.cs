using System.Text.Json;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Incidents;

/// <summary>
/// Turns a plugin quarantine into a security incident (RFC 09 rule 5).
/// </summary>
/// <remarks>
/// The residue from <c>docs/adr/0056</c>. The plugin registry detects the things worth an incident
/// — an output shaped like a credential, an effect that was never declared, a manifest that stopped
/// verifying — and could not raise one, because the incident service disables plugins and the
/// reverse edge would be a dependency cycle.
/// <para>
/// The bus breaks it. The registry publishes a fact; this consumes it. Neither knows about the
/// other, and the coupling that would have been a cycle is now a declared event type with a
/// contract (<c>docs/adr/0063</c>).
/// </para>
/// </remarks>
public sealed class QuarantineIncidentConsumer : IEventConsumer
{
    private readonly IIncidentService _incidents;

    public QuarantineIncidentConsumer(IIncidentService incidents)
    {
        _incidents = incidents;
    }

    public string Name => "incidents";

    public IReadOnlyList<string> EventTypes => [EventCatalogue.PluginQuarantined];

    public async Task<ConsumeResult> ConsumeAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        if (domainEvent.Type != EventCatalogue.PluginQuarantined)
        {
            return new ConsumeResult(ConsumeOutcome.Skipped);
        }

        (var resource, var reason) = Read(domainEvent);

        await _incidents.OpenAsync(
            new SecurityEvent(
                string.Empty,

                // A secret in a plugin's output is a different kind of event from a plugin that
                // failed three times, and only one of them is worth revoking the owner's standing
                // consent over.
                reason.Contains(PluginRefusal.SecretInOutput, StringComparison.OrdinalIgnoreCase)
                    ? SecuritySeverity.High
                    : SecuritySeverity.Medium,
                reason.Contains(PluginRefusal.SecretInOutput, StringComparison.OrdinalIgnoreCase)
                    ? SecurityEventType.SecretExposed
                    : SecurityEventType.UndeclaredBehaviour,
                domainEvent.CorrelationId,
                "plugins",

                // Named, so containment disables this plugin and not every plugin.
                resource,
                DecisionRef: null,
                EvidenceRef: domainEvent.EventId,
                DetectedAtUtc: domainEvent.OccurredAtUtc),
            ct).ConfigureAwait(false);

        return new ConsumeResult(ConsumeOutcome.Acked);
    }

    /// <summary>
    /// The plugin and why, from the event's payload.
    /// </summary>
    /// <remarks>
    /// Falls back rather than throwing. An event whose payload cannot be read is still an event
    /// worth an incident — losing it because a field was renamed would be the worst possible
    /// trade.
    /// </remarks>
    private static (string ResourceRef, string Reason) Read(DomainEvent domainEvent)
    {
        // The aggregate ref is already "plugin/{id}", which is the shape containment expects.
        var resource = domainEvent.AggregateRef ?? "plugin/unknown";

        if (domainEvent.PayloadJson is not { } payload)
        {
            return (resource, "quarantined");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);

            return (
                resource,
                document.RootElement.TryGetProperty("reason", out JsonElement reason)
                    ? reason.GetString() ?? "quarantined"
                    : "quarantined");
        }
        catch (JsonException)
        {
            return (resource, "quarantined");
        }
    }
}
