using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>A single executable capability. In It.0 all capabilities are read-only stubs.</summary>
public interface ICapability
{
    CapabilityDescriptor Descriptor { get; }

    ValueTask<JsonElement> ExecuteAsync(JsonElement input, CancellationToken ct);
}

/// <summary>Lookup and listing over the capability catalog.</summary>
public interface ICapabilityRegistry
{
    IReadOnlyList<CapabilityDescriptor> List(string? query);

    bool TryGet(string actionId, [NotNullWhen(true)] out ICapability? capability);
}

/// <summary>Invokes a resolved capability. A seam for cross-cutting concerns (timeouts, It.3 metrics).</summary>
public interface ICapabilityExecutor
{
    ValueTask<JsonElement> ExecuteAsync(ICapability capability, JsonElement input, CancellationToken ct);
}
