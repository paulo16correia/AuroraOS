using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// Supplies a secret a plugin cannot run without.
/// </summary>
/// <remarks>
/// A seam rather than a direct dependency on the vault, so the host that starts processes does not
/// also hold the ability to read every secret Aurora keeps. It asks for one by the name the
/// manifest declared and gets that one or nothing.
/// </remarks>
public interface IPluginSecretSource
{
    /// <summary>
    /// The value for <paramref name="name"/>, or <see langword="null"/> if it was never supplied.
    /// </summary>
    /// <remarks>
    /// Returns the value rather than a lease because it is about to cross a process boundary, where
    /// no lease can follow it. The caller writes it to a pipe and drops it.
    /// </remarks>
    Task<string?> FindAsync(string pluginId, string name, CancellationToken ct);
}

/// <summary>
/// Something a plugin says happened, before Aurora has decided what it means.
/// </summary>
/// <remarks>
/// Every field is the plugin's word. A service plugin holds a connection to somewhere outside this
/// machine and everything arriving over it was written by somebody else — so this is an observation
/// and never an instruction, whatever the text inside it says (RFC 09).
/// </remarks>
public sealed record PluginObservation(
    string PluginId,
    /// <summary>The plugin's name for what happened. Namespaced by the plugin id when published.</summary>
    string Kind,
    string PayloadJson,
    /// <summary>How sensitive the plugin says this is. Clamped to its declared ceiling.</summary>
    string SensitivityClass);

/// <summary>Receives what a service plugin reports, unprompted.</summary>
public interface IPluginObservationSink
{
    Task ReceiveAsync(PluginObservation observation, CancellationToken ct);
}

/// <summary>
/// Runs the plugins that hold a connection rather than answering one call.
/// </summary>
/// <remarks>
/// Separate from <see cref="IPluginHost"/> because the lifecycle is the whole difference: a
/// one-shot plugin has none, and a service has to be started, watched, restarted and stopped. Both
/// are invoked the same way, which is what keeps the kernel from having to know which it is talking
/// to.
/// </remarks>
public interface IPluginServiceSupervisor
{
    /// <summary>Starts it if it is not running. Safe to call for a plugin that has no service.</summary>
    Task<PluginServiceState> EnsureRunningAsync(
        PluginManifest manifest, bool networkGranted, CancellationToken ct);

    /// <summary>Stops it, and does not restart it.</summary>
    Task StopAsync(string pluginId, CancellationToken ct);

    /// <summary>What is running now, for the console and the operator surface.</summary>
    IReadOnlyList<PluginServiceState> Running();
}

/// <summary>What is known about one service plugin's process.</summary>
public sealed record PluginServiceState(
    string PluginId,
    /// <summary>One of <see cref="PluginServiceStatus"/>.</summary>
    string Status,
    int ConsecutiveFailures,
    string? Detail = null,
    string? StartedAtUtc = null);

/// <summary>The states a service plugin's process can be in.</summary>
public static class PluginServiceStatus
{
    public const string Stopped = "STOPPED";
    public const string Starting = "STARTING";
    public const string Ready = "READY";

    /// <summary>Died and is waiting out its backoff before being started again.</summary>
    public const string Restarting = "RESTARTING";

    /// <summary>
    /// Will not stay up, and is no longer being started.
    /// </summary>
    /// <remarks>
    /// A service that cannot run is not fixed by running it again. Past its ceiling it is held and
    /// a person decides, because a crash loop nobody sees is indistinguishable from working.
    /// </remarks>
    public const string Failed = "FAILED";
}
