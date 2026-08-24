namespace Aurora.Core.Contracts;

/// <summary>Functional domains a capability can belong to (RFC 051).</summary>
public static class CapabilityDomain
{
    public const string Communication = "COMMUNICATION";
    public const string Research = "RESEARCH";
    public const string Scheduling = "SCHEDULING";
    public const string Development = "DEVELOPMENT";
    public const string Files = "FILES";
    public const string Automation = "AUTOMATION";
}

public static class CapabilityRequestStatus
{
    public const string Requested = "REQUESTED";
    public const string Resolved = "RESOLVED";

    /// <summary>No permitted realisation. Blocked names what is missing; it never falls back to a shell.</summary>
    public const string Blocked = "BLOCKED";

    public const string Executed = "EXECUTED";
    public const string Failed = "FAILED";
}

/// <summary>
/// A functional need, stated without naming a supplier (RFC 051).
/// </summary>
/// <remarks>
/// The Mind asks to communicate or to search. Which application performs it is the Kernel's
/// decision, which is what keeps an intention from being welded to Gmail or Discord forever.
/// </remarks>
public sealed record CapabilityDefinition(
    string Id,
    string Domain,
    string IntentSchema,
    IReadOnlyList<string> EffectClasses,
    string RiskClass,
    IReadOnlyList<string> RequiredPermissions);

/// <summary>One concrete way a capability can be realised (RFC 051).</summary>
public sealed record CapabilityProvider(
    string Id,
    string CapabilityId,
    string ApplicationId,
    string ToolRef,
    int Priority,
    bool Available,
    double CostEstimate,
    IReadOnlyList<string> DataClasses,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> DeclaredEffects,
    string? HealthRef = null);

/// <summary>
/// A request for a capability.
/// </summary>
/// <remarks>
/// <see cref="PinnedProviderId"/> is how RFC 051 rule 1's exception is expressed: the user asked for
/// a specific destination or service, so no substitute is acceptable however available it is.
/// <see cref="PreferredProviderId"/> is a leaning, not a demand.
/// </remarks>
public sealed record CapabilityRequest(
    string Id,
    string? DecisionRef,
    string CapabilityId,
    string IntentPayloadJson,
    IReadOnlyList<string> TargetConstraints,
    string Status,
    string? PinnedProviderId = null,
    string? PreferredProviderId = null,
    string? ResolvedProviderId = null,
    string? BlockedReason = null);

/// <summary>Why one provider was chosen and each other was not (RFC 051).</summary>
public sealed record ResolutionReport(
    string RequestId,
    string? ChosenProviderId,
    IReadOnlyList<ProviderVerdict> Verdicts,
    string Explanation);

public sealed record ProviderVerdict(string ProviderId, bool Eligible, string Reason);

/// <summary>Reasons a provider was set aside, kept as constants so a report reads consistently.</summary>
public static class ResolutionReason
{
    public const string Chosen = "chosen";
    public const string Unavailable = "unavailable";
    public const string MissingPermission = "missing_permission";
    public const string EffectsExceedManifest = "effects_exceed_capability_manifest";
    public const string ConstraintUnmet = "constraint_unmet";
    public const string OverCostCeiling = "over_cost_ceiling";
    public const string NotThePinnedProvider = "not_the_pinned_provider";
    public const string LowerPriority = "lower_priority";
}

/// <summary>What the resolver is allowed to consider (RFC 051 rule 2).</summary>
public sealed record ResolutionContext(
    IReadOnlyList<string> GrantedPermissions,
    double CostCeiling,
    IReadOnlyList<string> AllowedDataClasses);

public sealed class CapabilityResolutionException : Exception
{
    public CapabilityResolutionException(string message) : base(message)
    {
    }
}
