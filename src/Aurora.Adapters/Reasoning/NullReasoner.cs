using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Reasoning;

/// <summary>
/// It.0 has no reasoner, so objective-mode is unavailable. Always returns no proposal,
/// forcing callers onto the explicit action-id path.
/// </summary>
public sealed class NullReasoner : IReasoner
{
    public ValueTask<ReasonerProposal?> ProposeAsync(
        string objective,
        IReadOnlyList<CapabilityDescriptor> catalog,
        CancellationToken ct) => ValueTask.FromResult<ReasonerProposal?>(null);
}
