using System.Diagnostics.CodeAnalysis;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Capabilities;

/// <summary>
/// Aurora's own capabilities and the installed plugins', in one catalogue.
/// </summary>
/// <remarks>
/// Aurora's own win a collision, and the collision is not supposed to happen: the manifest reader
/// refuses a key that already exists. This is the second line, for the case where a plugin was
/// installed before a built-in capability of the same name existed. A plugin silently shadowing
/// <c>files.write_sandbox</c> would be the most valuable bug in the system to an attacker.
/// </remarks>
public sealed class CompositeCapabilityRegistry : ICapabilityRegistry
{
    private readonly ICapabilityRegistry _built;
    private readonly ICapabilityRegistry _plugins;

    public CompositeCapabilityRegistry(ICapabilityRegistry built, ICapabilityRegistry plugins)
    {
        _built = built;
        _plugins = plugins;
    }

    public IReadOnlyList<CapabilityDescriptor> List(string? query)
    {
        List<CapabilityDescriptor> own = [.. _built.List(query)];
        var taken = own.Select(d => d.ActionId).ToHashSet(StringComparer.Ordinal);

        return [.. own, .. _plugins.List(query).Where(d => !taken.Contains(d.ActionId))];
    }

    public bool TryGet(string actionId, [NotNullWhen(true)] out ICapability? capability) =>
        _built.TryGet(actionId, out capability) || _plugins.TryGet(actionId, out capability);
}
