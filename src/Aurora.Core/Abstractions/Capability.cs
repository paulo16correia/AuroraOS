using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>Selects a permitted realisation for a stated capability (RFC 051).</summary>
public interface ICapabilityResolver
{
    Task<CapabilityDefinition> RegisterCapabilityAsync(CapabilityDefinition definition, CancellationToken ct);

    /// <summary>
    /// Registers a provider. Refuses one whose declared effects exceed the capability's manifest
    /// (rule 3): a provider cannot smuggle in an effect the capability never claimed.
    /// </summary>
    Task<CapabilityProvider> RegisterProviderAsync(CapabilityProvider provider, CancellationToken ct);

    /// <summary>
    /// Resolves a request to one provider, or blocks. Never substitutes for a pinned provider, and
    /// never falls back to something generic when nothing fits.
    /// </summary>
    Task<CapabilityRequest> ResolveAsync(
        CapabilityRequest request, ResolutionContext context, CancellationToken ct);

    /// <summary>
    /// Reports a provider failure and offers an alternative only where it preserves the intention,
    /// scope and policy of the original request (rule 4).
    /// </summary>
    Task<CapabilityRequest> HandleProviderFailureAsync(
        string requestId, string reason, ResolutionContext context, CancellationToken ct);

    Task<ResolutionReport> ExplainResolutionAsync(string requestId, CancellationToken ct);

    Task<CapabilityRequest?> GetRequestAsync(string requestId, CancellationToken ct);
}
