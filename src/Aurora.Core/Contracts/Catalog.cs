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
    bool ApprovalRequired,
    /// <summary>
    /// Whether a completed call can be undone by whoever made it.
    /// </summary>
    /// <remarks>
    /// Not a claim that Aurora will undo it — a claim that the caller is given what they need to.
    /// Default <see langword="false"/>, because the honest answer for a capability whose author
    /// did not think about it is that they did not.
    /// <para>
    /// Policy reads this: a HIGH capability is permitted only when it is both approval-gated and
    /// reversible. Approval is a person saying yes once, and at HIGH that is not enough on its own
    /// — the Constitution's Article 3 asks the same question of the decision.
    /// </para>
    /// </remarks>
    bool Reversible = false);

/// <summary>Result of <c>aurora_catalog</c>.</summary>
public sealed record CatalogResult(IReadOnlyList<CapabilityDescriptor> Actions);
