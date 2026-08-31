using System.Text.Json;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Plugins;

/// <summary>
/// One capability offered by an installed plugin, as an ordinary Aurora capability.
/// </summary>
/// <remarks>
/// The point of this class is that there is nothing special about it. A plugin's capability appears
/// in <c>aurora_catalog</c>, is judged by the same policy engine, needs the same persisted
/// approval, runs through the same cognitive cycle, and lands in the same audit log — because it
/// goes through the same seam Aurora's own capabilities go through.
/// <para>
/// The alternative, a separate path for plugins, is how a system ends up with two sets of rules
/// and only remembers to update one of them.
/// </para>
/// </remarks>
public sealed class PluginCapabilityBridge : ICapability
{
    /// <summary>
    /// Refusals that mean a plugin reached for authority it was not given, rather than that
    /// something went wrong.
    /// </summary>
    /// <remarks>
    /// The manifest reader refuses an undeclared permission at install and the catalogue refuses
    /// an unknown action, so a call arriving here with one of these got past both.
    /// </remarks>
    private static readonly string[] Escalations =
    [
        PluginRefusal.PermissionNotGranted,
        PluginRefusal.UndeclaredEffect,
        PluginRefusal.AboveDeclaredClassification,
    ];

    private readonly IPluginRegistry _registry;
    private readonly ISecurityWatch _watch;
    private readonly string _pluginId;
    private readonly string _capabilityKey;
    private readonly string _dataClass;

    public PluginCapabilityBridge(
        IPluginRegistry registry, ISecurityWatch watch,
        PluginManifest manifest, PluginCapability capability)
    {
        _registry = registry;
        _watch = watch;
        _pluginId = manifest.PluginId;
        _capabilityKey = capability.Key;
        _dataClass = manifest.MaxDataClass;

        Descriptor = new CapabilityDescriptor(
            ActionId: capability.Key,
            Title: capability.Title,

            // Who wrote it, in the description a person reads before allowing it. A plugin's
            // capability that looked exactly like one of Aurora's own would be the wrong thing to
            // show somebody at an approval prompt.
            Description: $"{capability.Description} (plugin {manifest.PluginId} by {manifest.Publisher})",
            InputSchema: Schema(capability.InputSchema),
            Effects: capability.Effects,
            Risk: capability.Risk,
            ApprovalRequired: capability.ApprovalRequired,
            Reversible: capability.Reversible,
            OpensWindow: capability.OpensWindow);
    }

    public CapabilityDescriptor Descriptor { get; }

    public async ValueTask<JsonElement> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        // Through the registry rather than straight to the host: that is where the permission
        // check, the classification ceiling, the circuit breaker and the secret-shaped-output
        // refusal live, and none of them are things a caller should be able to route around.
        PluginResult result = await _registry.InvokeAsync(
            new PluginInvocation(_pluginId, _capabilityKey, input.GetRawText(), _dataClass), ct)
            .ConfigureAwait(false);

        if (!result.Ok)
        {
            if (result.Refusal is { } refusal && Escalations.Contains(refusal, StringComparer.Ordinal))
            {
                await _watch.PrivilegeEscalationAsync(
                    _pluginId, $"plugin/{_pluginId}", $"{refusal}: {result.Detail}", ct)
                    .ConfigureAwait(false);
            }

            throw new PluginException(
                $"{_capabilityKey} did not complete: {result.Refusal ?? "no reason given"} — {result.Detail}");
        }

        if (string.IsNullOrWhiteSpace(result.OutputJson))
        {
            throw new PluginException($"{_capabilityKey} returned nothing.");
        }

        try
        {
            return JsonDocument.Parse(result.OutputJson).RootElement.Clone();
        }
        catch (JsonException)
        {
            // RFC 06 rule 3: an external result is untrusted until it validates. A plugin that
            // wrote something other than JSON has failed the call, not produced a string result.
            throw new PluginException($"{_capabilityKey} did not return JSON.");
        }
    }

    private static JsonElement Schema(string json)
    {
        try
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException)
        {
            // Unreachable through the manifest reader, which refuses a non-object schema. Kept
            // because this constructor is also reachable from a manifest already in the database,
            // written by an older Aurora.
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }
}
