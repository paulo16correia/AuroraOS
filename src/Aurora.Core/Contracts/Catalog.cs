using System.Text.Json;

namespace Aurora.Core.Contracts;

/// <summary>Risk tier of a capability. Governs consent: LOW auto, MEDIUM+ requires approval.</summary>
public enum RiskLevel
{
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>A single capability as advertised by <c>aurora_catalog</c>.</summary>
public sealed record CapabilityDescriptor(
    string ActionId,
    string Title,
    string Description,
    JsonElement InputSchema,
    IReadOnlyList<string> Effects,
    RiskLevel Risk,
    bool ApprovalRequired);

/// <summary>Result of <c>aurora_catalog</c>.</summary>
public sealed record CatalogResult(IReadOnlyList<CapabilityDescriptor> Actions);
