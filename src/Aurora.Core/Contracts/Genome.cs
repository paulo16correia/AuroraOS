namespace Aurora.Core.Contracts;

/// <summary>Publication state of a genome (RFC 036).</summary>
public static class GenomeStatus
{
    public const string Draft = "DRAFT";
    public const string Released = "RELEASED";
    public const string Retired = "RETIRED";
}

/// <summary>
/// The versioned manifest of everything an instance receives at birth (RFC 036).
/// </summary>
/// <remarks>
/// Rule 2: it holds no user memory, credentials, relationships, beliefs or execution state. Every
/// field here is either a version, a reference to a template, or a list of identifiers — nothing
/// that could carry acquired data, which is what keeps a genome reproducible and shareable.
/// </remarks>
public sealed record Genome(
    string Id,
    string Family,
    string Version,
    string? ParentGenomeRef,
    string Status,
    string ConstitutionVersion,
    string LawSetVersion,
    string BaseIdentityTemplateRef,
    string PersonalityBaselineRef,
    string DevelopmentProfileRef,
    int MindSchemaVersion,
    IReadOnlyList<string> AllowedCapabilityIds,
    IReadOnlyList<string> PolicyBundleRefs,
    IReadOnlyList<string> DefaultLocales,
    string BootstrapConfigurationRef,
    string IntegrityHash,
    string Signature);

/// <summary>A proposed change to a genome for one installation (RFC 036).</summary>
public sealed record GenomeOverride(string Field, IReadOnlyList<string> Values);

/// <summary>Outcome of <c>Genome.validateOverride</c>.</summary>
public enum OverrideVerdict
{
    /// <summary>The change only restricts; it is safe to apply.</summary>
    Allow,

    /// <summary>The change would relax the Constitution, the Laws or a security guarantee.</summary>
    Deny,

    /// <summary>Not obviously a restriction. A person decides; the resolver never guesses.</summary>
    Review,
}

public sealed record OverrideDecision(OverrideVerdict Verdict, string Reason);

/// <summary>What one installation actually got, and what it was refused (RFC 036).</summary>
public sealed record GenomeResolution(
    string Id,
    string GenomeId,
    string InstallationId,
    IReadOnlyList<string> SelectedVariants,
    IReadOnlyList<string> EffectiveCapabilityIds,
    IReadOnlyList<string> DeniedOverrides,
    string EffectiveHash,
    string ResolvedAtUtc,
    string Resolver);

public static class BootstrapStatus
{
    public const string Ready = "READY";

    /// <summary>Some capabilities are unavailable; the instance starts with less, never with a substitute.</summary>
    public const string Degraded = "DEGRADED";

    public const string Blocked = "BLOCKED";
}

/// <summary>The plan produced from a resolution (RFC 036).</summary>
public sealed record BootstrapPlan(
    string ResolutionId,
    string Status,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> AvailableCapabilityIds,
    IReadOnlyList<string> MissingCapabilityIds);

/// <summary>Raised when a genome cannot be used at all.</summary>
public sealed class GenomeException : Exception
{
    public GenomeException(string message) : base(message)
    {
    }
}
