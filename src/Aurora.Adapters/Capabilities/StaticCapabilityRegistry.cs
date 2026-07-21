using System.Diagnostics.CodeAnalysis;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Capabilities;

/// <summary>
/// In-memory <see cref="ICapabilityRegistry"/> over a fixed set of capabilities supplied at
/// construction. Lookup is ordinal; listing preserves construction order.
/// </summary>
public sealed class StaticCapabilityRegistry : ICapabilityRegistry
{
    private readonly Dictionary<string, ICapability> _byActionId;
    private readonly IReadOnlyList<CapabilityDescriptor> _descriptors;

    public StaticCapabilityRegistry(IEnumerable<ICapability> capabilities)
    {
        _byActionId = new Dictionary<string, ICapability>(StringComparer.Ordinal);
        var descriptors = new List<CapabilityDescriptor>();
        foreach (ICapability capability in capabilities)
        {
            _byActionId.Add(capability.Descriptor.ActionId, capability);
            descriptors.Add(capability.Descriptor);
        }

        _descriptors = descriptors;
    }

    public IReadOnlyList<CapabilityDescriptor> List(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return _descriptors;
        }

        var matches = new List<CapabilityDescriptor>();
        foreach (CapabilityDescriptor descriptor in _descriptors)
        {
            if (descriptor.ActionId.Contains(query, StringComparison.OrdinalIgnoreCase)
                || descriptor.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || descriptor.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(descriptor);
            }
        }

        return matches;
    }

    public bool TryGet(string actionId, [NotNullWhen(true)] out ICapability? capability)
        => _byActionId.TryGetValue(actionId, out capability);
}
