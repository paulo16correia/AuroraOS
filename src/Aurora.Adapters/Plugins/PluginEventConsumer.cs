using Aurora.Core;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Plugins;

/// <summary>
/// Delivers events to the plugins that subscribed to them (RFC 060).
/// </summary>
/// <remarks>
/// A plugin's <c>event_subscriptions</c> were declared, filtered correctly by
/// <see cref="IPluginRegistry.PermittedSubscriptionsAsync"/>, and delivered to nobody. This is the
/// other half.
/// <para>
/// A subscription is not an invitation to act. What arrives is a fact, through the same invocation
/// path a call takes — so the same permission check, the same classification ceiling, the same
/// circuit breaker and the same sandbox apply. A plugin that wants to <i>do</i> something about an
/// event asks Aurora for a capability, and that goes through policy and approval like anything
/// else.
/// </para>
/// </remarks>
public sealed class PluginEventConsumer : IEventConsumer
{
    /// <summary>
    /// The capability key a subscribed plugin is invoked on.
    /// </summary>
    /// <remarks>
    /// One key rather than one per event type: a plugin declares which events it wants and is
    /// handed them here, so the routing it would otherwise duplicate stays in one place. The event
    /// type is in the payload.
    /// </remarks>
    public const string OnEvent = "on_event";

    private readonly IPluginRegistry _registry;

    public PluginEventConsumer(IPluginRegistry registry)
    {
        _registry = registry;
    }

    public string Name => "plugins";

    public async Task<ConsumeResult> ConsumeAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        IReadOnlyList<PluginInstallation> installed =
            await _registry.ListAsync(ct).ConfigureAwait(false);

        var failures = new List<string>();

        foreach (PluginInstallation plugin in installed.Where(
            p => p.Status == InstallationStatus.Installed))
        {
            IReadOnlyList<string> permitted =
                await _registry.PermittedSubscriptionsAsync(plugin.PluginId, ct).ConfigureAwait(false);

            if (!permitted.Contains(domainEvent.Type, StringComparer.Ordinal))
            {
                continue;
            }

            // Never the payload of a classified event: RFC 050 rule 3 says those travel by
            // reference, and a plugin is the last thing that should be handed one directly.
            var body = AuroraJson.Serialize(new
            {
                type = domainEvent.Type,
                schema_version = domainEvent.SchemaVersion,
                event_id = domainEvent.EventId,
                correlation_id = domainEvent.CorrelationId,
                occurred_at_utc = domainEvent.OccurredAtUtc,
                aggregate_ref = domainEvent.AggregateRef,
                payload = Sensitivity.RequiresReference(domainEvent.SensitivityClass)
                    ? null
                    : domainEvent.PayloadJson,
            });

            PluginResult result = await _registry.InvokeAsync(
                new PluginInvocation(plugin.PluginId, OnEvent, body, domainEvent.SensitivityClass),
                ct).ConfigureAwait(false);

            if (!result.Ok)
            {
                failures.Add($"{plugin.PluginId}: {result.Refusal ?? result.Detail}");
            }
        }

        // One plugin failing does not fail the delivery for the others, and it does not make the
        // event undelivered: the registry has already counted the failure against that plugin, and
        // three of them open its circuit. Retrying the whole fan-out would punish the plugins that
        // handled it correctly by handing them the same event again.
        return failures.Count == 0
            ? new ConsumeResult(ConsumeOutcome.Acked)
            : new ConsumeResult(ConsumeOutcome.Acked, string.Join("; ", failures));
    }
}
