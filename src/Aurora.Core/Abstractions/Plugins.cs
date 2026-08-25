using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// Runs a plugin's code, somewhere that is not this process.
/// </summary>
/// <remarks>
/// A seam, and the honest boundary of what Aurora can promise. The contract above it is enforced
/// whatever the host does; the isolation is only as good as the host, and the host is where the
/// platform-specific work lives.
/// </remarks>
public interface IPluginHost
{
    Task<PluginResult> InvokeAsync(
        PluginManifest manifest, PluginInvocation invocation, CancellationToken ct);
}

/// <summary>
/// Third-party capabilities, on a security contract rather than as a privileged exception (RFC 060).
/// </summary>
/// <remarks>
/// The point of the whole RFC in one sentence from its own justification: a plugin for one thing
/// can be useful without gaining powers over email, SSH or the Mind. Everything here exists to keep
/// a declaration from becoming an authority — a manifest states limits, and Aurora enforces exactly
/// those limits and nothing softer.
/// </remarks>
public interface IPluginRegistry
{
    /// <summary>
    /// Checks a manifest against its signature, its hash and this platform. Nothing runs first.
    /// </summary>
    Task<PluginVerification> VerifyAsync(PluginManifest manifest, CancellationToken ct);

    /// <summary>
    /// Installs a verified plugin. Needs an approval, and grants exactly what was reviewed (rule 3).
    /// </summary>
    Task<PluginInstallation> InstallAsync(
        PluginManifest manifest, IReadOnlyList<string> grantedPermissions,
        string approvalRef, CancellationToken ct);

    /// <summary>
    /// Applies an update. New permissions or a new publisher send it to quarantine (rule 5).
    /// </summary>
    Task<PluginInstallation> UpdateAsync(PluginManifest manifest, CancellationToken ct);

    /// <summary>
    /// Invokes a capability, refusing anything the manifest did not declare.
    /// </summary>
    Task<PluginResult> InvokeAsync(PluginInvocation invocation, CancellationToken ct);

    Task<PluginInstallation> DisableAsync(string installationId, string actor, CancellationToken ct);

    /// <summary>Releases a quarantine, which is a decision and needs an approval.</summary>
    Task<PluginInstallation> ReleaseAsync(
        string installationId, string approvalRef, string actor, CancellationToken ct);

    /// <summary>Which declared events this plugin may actually receive, given what was granted.</summary>
    Task<IReadOnlyList<string>> PermittedSubscriptionsAsync(string pluginId, CancellationToken ct);

    Task<PluginInstallation?> GetAsync(string pluginId, CancellationToken ct);

    Task<IReadOnlyList<PluginInstallation>> ListAsync(CancellationToken ct);
}
