namespace Aurora.Core.Contracts;

public static class InstallationStatus
{
    public const string Installed = "INSTALLED";
    public const string Disabled = "DISABLED";

    /// <summary>Held. Something about it changed or misbehaved and it does not run until reviewed.</summary>
    public const string Quarantined = "QUARANTINED";

    public const string Removed = "REMOVED";

    public static bool CanRun(string status) => status == Installed;
}

/// <summary>Why a plugin was refused. A closed set, so a refusal is checkable.</summary>
public static class PluginRefusal
{
    public const string SignatureInvalid = "SIGNATURE_INVALID";
    public const string PlatformTooOld = "PLATFORM_TOO_OLD";
    public const string IntegrityMismatch = "INTEGRITY_MISMATCH";
    public const string UndeclaredEffect = "UNDECLARED_EFFECT";
    public const string UndeclaredEndpoint = "UNDECLARED_ENDPOINT";
    public const string UndeclaredDataClass = "UNDECLARED_DATA_CLASS";
    public const string PermissionNotGranted = "PERMISSION_NOT_GRANTED";
    public const string AboveDeclaredClassification = "ABOVE_DECLARED_CLASSIFICATION";
    public const string SecretInOutput = "SECRET_IN_OUTPUT";
    public const string CircuitOpen = "CIRCUIT_OPEN";
    public const string NewPermissions = "NEW_PERMISSIONS_REQUIRE_REVIEW";
    public const string NewPublisher = "NEW_PUBLISHER_REQUIRES_REVIEW";

    /// <summary>
    /// The machine could not confine the plugin, and the owner has not accepted running it loose
    /// (docs/adr/0052). Nothing about the plugin is wrong, so this does not count against it.
    /// </summary>
    public const string SandboxUnavailable = "sandbox_unavailable";
}

/// <summary>
/// One thing a plugin offers, declared completely before it runs (RFC 060).
/// </summary>
/// <remarks>
/// Everything a capability may do is on this record. RFC 060 rule 1 is that an undeclared dynamic
/// request is denied, so a field that is empty here is not "unspecified" — it is "none", and it is
/// enforced as none.
/// </remarks>
public sealed record PluginCapability(
    string Key,
    string InputSchema,
    string OutputSchema,
    IReadOnlyList<string> Effects,
    bool ApprovalRequired,
    int RateLimitPerMinute,
    TimeSpan Timeout,
    bool IdempotencySupport,
    string AuditLevel,
    /// <summary>What a person reads in the catalogue before deciding to allow it.</summary>
    string Title = "",
    string Description = "",
    /// <summary>
    /// How much is at stake, judged by the same policy that judges Aurora's own capabilities.
    /// </summary>
    /// <remarks>
    /// Declared by the plugin and therefore a claim — but a claim that only ever costs it. Policy
    /// reads risk together with approval and reversibility, so understating it does not widen what
    /// a plugin may do; it is the declared effects and the granted permissions that bound that.
    /// Defaults to <see cref="RiskLevel.High"/>, because a plugin whose author did not say is not
    /// one to assume the best of.
    /// </remarks>
    RiskLevel Risk = RiskLevel.High,
    /// <summary>Whether a completed call can be undone by whoever made it.</summary>
    bool Reversible = false);

/// <summary>
/// Everything a plugin says about itself, before anybody decides whether to believe it.
/// </summary>
/// <remarks>
/// A manifest is a claim, not a permission. It is signed so it can be attributed, hashed so it
/// cannot be edited after the fact, and every claim in it becomes a limit rather than a licence:
/// declaring a network endpoint does not grant reaching it, it makes reaching anything else a
/// refusal.
/// </remarks>
public sealed record PluginManifest(
    string PluginId,
    string Version,
    string Publisher,
    string Signature,
    int MinPlatformVersion,
    IReadOnlyList<PluginCapability> Capabilities,
    IReadOnlyList<string> EventSubscriptions,
    IReadOnlyList<string> RequiredPermissions,
    /// <summary>The highest classification of data this plugin may ever be handed.</summary>
    string MaxDataClass,
    IReadOnlyList<string> NetworkEndpoints,
    string DocumentationRef,
    string IntegrityHash,
    /// <summary>The command that runs it, out of this process.</summary>
    string? Executable = null);

public sealed record PluginInstallation(
    string Id,
    string PluginId,
    string Version,
    string Publisher,
    string Status,
    IReadOnlyList<string> GrantedPermissions,
    string ManifestJson,
    string InstalledAtUtc,
    string UpdatedAtUtc,
    int ConsecutiveFailures,
    string? QuarantineReason = null,
    string? ApprovalRef = null);

/// <summary>What verification found, before anything is installed.</summary>
/// <remarks>Named apart from the Mind State one: they answer different questions about different things.</remarks>
public sealed record PluginVerification(
    bool Ok,
    IReadOnlyList<string> Refusals,
    string Detail);

/// <summary>A call a plugin is being asked to make, after Aurora has authorised it.</summary>
public sealed record PluginInvocation(
    string PluginId,
    string CapabilityKey,
    string InputJson,
    string DataClass);

public sealed record PluginResult(
    bool Ok,
    string? OutputJson,
    string? Refusal,
    string Detail,
    long DurationMs);

public sealed class PluginException : Exception
{
    public PluginException(string message) : base(message)
    {
    }
}
