using System.Text.Json;
using Aurora.Core.Abstractions;

namespace Aurora.Adapters.Capabilities;

/// <summary>
/// Baseline <see cref="ICapabilityExecutor"/> that invokes the capability directly. A seam for
/// cross-cutting concerns (timeouts, It.3 metrics) that arrive in later iterations.
/// </summary>
public sealed class CapabilityExecutor : ICapabilityExecutor
{
    public ValueTask<JsonElement> ExecuteAsync(ICapability capability, JsonElement input, CancellationToken ct)
        => capability.ExecuteAsync(input, ct);
}
