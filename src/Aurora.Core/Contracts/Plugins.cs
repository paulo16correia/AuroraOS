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
    /// <summary>
    /// The plugin reached a host it never declared, or asked for the network without a grant.
    /// </summary>
    /// <remarks>
    /// RFC 060 rule 1: a plugin declares the hosts it talks to, and an undeclared one is refused.
    /// Aurora held the strict reading until <c>docs/adr/0067</c> — the sandbox denied the network
    /// outright, so there was no endpoint to grant and any declaration was refused. An integration
    /// that must reach a service changed that: endpoints are now grantable, once, by the owner, and
    /// this is what a plugin gets for asking outside the grant.
    /// </remarks>
    public const string UndeclaredEndpoint = "UNDECLARED_ENDPOINT";

    /// <summary>The manifest declares network endpoints and the owner has not granted them.</summary>
    public const string NetworkNotGranted = "NETWORK_NOT_GRANTED";

    /// <summary>A secret the plugin needs is not in the vault, so it was never started.</summary>
    public const string SecretMissing = "SECRET_MISSING";

    /// <summary>The service would not start, or stopped answering.</summary>
    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";

    /// <summary>
    /// The call reached the plugin and its outcome is genuinely unknown.
    /// </summary>
    /// <remarks>
    /// Distinct from a failure, and the distinction carries real weight: a write whose answer never
    /// arrived may have been performed. Reporting it as failed invites a retry that does it twice;
    /// reporting it as done invites a caller to rely on something that may not exist. Aurora cannot
    /// tell from here, so it says so rather than guessing (<see cref="PluginOutcome.Unknown"/>).
    /// </remarks>
    public const string AmbiguousOutcome = "AMBIGUOUS_OUTCOME";
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
    /// <summary>
    /// The hosts this plugin talks to. Empty means it never leaves the machine.
    /// </summary>
    /// <remarks>
    /// Granted by the owner at install, once, naming every host. What the sandbox can enforce of
    /// this varies by platform and is documented rather than implied: see
    /// <c>docs/reference/platform-support.md</c>.
    /// </remarks>
    IReadOnlyList<string> NetworkEndpoints,
    string DocumentationRef,
    string IntegrityHash,
    /// <summary>The command that runs it, out of this process.</summary>
    string? Executable = null,
    /// <summary>Set when the plugin holds a connection rather than answering one call.</summary>
    PluginService? Service = null,
    /// <summary>Secrets it cannot run without, by name. Values never appear here.</summary>
    IReadOnlyList<PluginSecretRequirement>? RequiredSecrets = null);

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
    string? ApprovalRef = null,
    /// <summary>
    /// The hosts the owner agreed this plugin may reach. Empty means none, which is the default.
    /// </summary>
    /// <remarks>
    /// Stored on the installation rather than read from the manifest at call time: the manifest is
    /// what was asked for, and this is what was agreed to. An update that adds a host therefore
    /// needs a fresh decision instead of inheriting the old one.
    /// </remarks>
    IReadOnlyList<string>? GrantedEndpoints = null);

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
    string DataClass,
    /// <summary>
    /// The caller's key for this call, so a repeat is recognised as the same act.
    /// </summary>
    /// <remarks>
    /// Passed through to the plugin, which is the only party that can deduplicate against the
    /// service it talks to. Aurora cannot: it does not know whether a message was delivered, only
    /// whether it heard back.
    /// </remarks>
    string? IdempotencyKey = null,
    /// <summary>
    /// Whether this installation may leave the machine, as the owner agreed at install.
    /// </summary>
    /// <remarks>
    /// On the call rather than read from the manifest, because the manifest is what was asked for
    /// and the installation is what was agreed to. A plugin whose update declares a new host does
    /// not inherit the old grant.
    /// </remarks>
    bool NetworkGranted = false);

/// <summary>
/// What is known about a call after it returns.
/// </summary>
/// <remarks>
/// Three states rather than two, because an external effect has three. A write whose answer never
/// arrived is not a failure — the message may be sitting in the channel — and calling it one is how
/// a retry sends it twice.
/// </remarks>
public static class PluginOutcome
{
    public const string Completed = "completed";
    public const string Failed = "failed";

    /// <summary>Reached the outside and the result is not known. Never retried automatically.</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// A long-lived plugin process, for work that cannot be done in one call and exit.
/// </summary>
/// <remarks>
/// The plugin host runs one subprocess per invocation: start, write stdin, read stdout, wait for
/// exit. That is the right shape for a plugin that answers a question, and the wrong one for a
/// plugin that holds a connection — a gateway socket with heartbeats, or an audio stream, cannot be
/// re-established for every call and torn down after it.
/// <para>
/// A service is supervised rather than trusted: it is started when first needed, restarted with
/// backoff when it dies, killed when Aurora stops, and quarantined when it will not stay up. It
/// speaks the same JSON, one object per line, and everything it says is data.
/// </para>
/// </remarks>
public sealed record PluginService(
    /// <summary>The command that runs it, out of this process.</summary>
    string Executable,
    /// <summary>How long to wait for the ready frame before treating the start as failed.</summary>
    TimeSpan StartTimeout,
    /// <summary>
    /// How often Aurora expects to hear from it. A service that goes quiet is restarted.
    /// </summary>
    TimeSpan Heartbeat,
    /// <summary>
    /// How many failed starts in a row before the plugin is quarantined rather than restarted.
    /// </summary>
    /// <remarks>
    /// A service that cannot stay up is not fixed by starting it again; past this it is held, and
    /// a person decides. Without a ceiling a crashing plugin becomes a restart loop nobody sees.
    /// </remarks>
    int MaxConsecutiveFailures = 5);

/// <summary>One secret a plugin cannot run without.</summary>
/// <remarks>
/// Declared by name only. The value lives in Aurora's vault, is leased for the life of the process,
/// and is handed over the pipe in the opening frame rather than through the environment — a child's
/// environment is readable from outside the process on most systems, and a pipe is not.
/// </remarks>
public sealed record PluginSecretRequirement(
    string Name,
    /// <summary>What a person reads before deciding to supply it.</summary>
    string Purpose);

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
