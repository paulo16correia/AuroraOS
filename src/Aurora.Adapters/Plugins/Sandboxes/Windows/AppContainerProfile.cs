using System.Security.Cryptography;
using System.Text;
using Aurora.Core.Abstractions;

namespace Aurora.Adapters.Plugins.Sandboxes.Windows;

/// <summary>What a plugin's AppContainer is called, may do, and may reach.</summary>
/// <param name="Name">
/// The container name Windows files the profile under. Deterministic, so restarting Aurora reuses
/// the same container rather than accumulating one per run.
/// </param>
/// <param name="DisplayName">What an administrator sees in the profile list.</param>
/// <param name="Capabilities">
/// Exhaustive. An AppContainer starts with none, and every one added is authority the plugin did
/// not have a moment ago — so this list is derived from the owner's grants and from nothing else.
/// </param>
/// <param name="Grants">
/// The only paths the container may touch. An AppContainer cannot read the filesystem at all
/// unless its own SID is on the ACL, which makes the default deny real rather than configured.
/// </param>
public sealed record AppContainerProfile(
    string Name,
    string DisplayName,
    string Description,
    IReadOnlyList<AppContainerCapability> Capabilities,
    IReadOnlyList<AppContainerGrant> Grants);

/// <summary>
/// The Windows capabilities Aurora will ever ask for.
/// </summary>
/// <remarks>
/// One, at present. Windows publishes dozens — the camera, the microphone, the owner's documents,
/// their pictures — and a plugin has no business with any of them: what it needs from the machine
/// arrives through Aurora, which is the point of Aurora. The enum exists so that adding a second
/// is a change somebody makes deliberately and reviews, rather than a string appearing in a list.
/// </remarks>
public enum AppContainerCapability
{
    /// <summary>
    /// Outbound connections to the internet, and only there.
    /// </summary>
    /// <remarks>
    /// Windows draws a line the other two platforms do not: an AppContainer with this capability
    /// still cannot reach 127.0.0.1, because loopback is refused to app containers by default.
    /// So a plugin granted the network here can talk to Discord and cannot talk to Aurora's own
    /// MCP endpoint — a boundary that on macOS rests on the plugin not knowing the port.
    /// </remarks>
    InternetClient,
}

/// <summary>One path the container may reach, and how far.</summary>
public sealed record AppContainerGrant(string Path, AppContainerAccess Access);

/// <summary>How much of a path a container gets.</summary>
public enum AppContainerAccess
{
    /// <summary>Enough to run the program and read what ships beside it. No writing.</summary>
    ReadExecute,

    /// <summary>Read, write, delete. Exactly one directory ever gets this.</summary>
    Full,
}

/// <summary>
/// Derives a plugin's container from what the owner granted it.
/// </summary>
/// <remarks>
/// Pure, and separate from the code that creates the container, because this is the part that
/// decides how much authority a plugin gets and it can be tested on any machine. What cannot be
/// tested away from Windows is whether the kernel then honours it — see
/// <c>docs/reference/platform-support.md</c>, which says so rather than implying otherwise.
/// </remarks>
public static class AppContainerProfiles
{
    /// <summary>Windows' limit on a container name.</summary>
    private const int MaxNameLength = 64;

    private const string Prefix = "aurora.plugin.";

    public static AppContainerProfile For(SandboxRequest request)
    {
        var capabilities = new List<AppContainerCapability>();

        if (request.NetworkGranted)
        {
            capabilities.Add(AppContainerCapability.InternetClient);
        }

        // The graphics processor is deliberately absent. It is granted on macOS by opening the
        // IOKit surface, and Windows offers no capability that means the same thing — so a plugin
        // that asked for it gets a container without it and finds out from its own failure, which
        // is the honest outcome until somebody implements and verifies the equivalent.

        var program = Path.GetDirectoryName(Path.GetFullPath(request.Executable));

        var grants = new List<AppContainerGrant>
        {
            // Its own directory: everything it writes, and the only place it may.
            new(Path.GetFullPath(request.WorkingDirectory), AppContainerAccess.Full),
        };

        if (!string.IsNullOrEmpty(program)
            && !string.Equals(
                program,
                Path.GetFullPath(request.WorkingDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            // Where the program itself lives, read-only. Without it the container cannot execute
            // the plugin at all; with more than read-and-execute, a plugin could rewrite its own
            // installed code and the manifest hash would be describing something that no longer
            // runs.
            grants.Add(new AppContainerGrant(program, AppContainerAccess.ReadExecute));
        }

        return new AppContainerProfile(
            NameFor(request.PluginId),
            $"Aurora plugin {request.PluginId}",
            $"Confines the Aurora plugin {request.PluginId}. Created by Aurora; safe to delete "
            + "when the plugin is uninstalled.",
            capabilities,
            grants);
    }

    /// <summary>
    /// A container name Windows accepts, that is the same every time for the same plugin.
    /// </summary>
    /// <remarks>
    /// Windows allows letters, digits, dots, dashes and underscores, up to 64 characters. A plugin
    /// id is <c>publisher/name</c> and fits none of that, so it is transliterated — and a
    /// transliteration collides: <c>acme/note-s</c> and <c>acme/note_s</c> would become the same
    /// container and therefore the same grants. The hash of the original id is what keeps two
    /// plugins from sharing a confinement, so it is not decoration and must not be dropped to make
    /// the name prettier.
    /// </remarks>
    public static string NameFor(string pluginId)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(pluginId)))[..12].ToLowerInvariant();

        var safe = new StringBuilder();

        foreach (var character in pluginId)
        {
            safe.Append(char.IsAsciiLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-');
        }

        var room = MaxNameLength - Prefix.Length - hash.Length - 1;
        var body = safe.ToString().Trim('-');

        if (body.Length > room)
        {
            body = body[..room];
        }

        return $"{Prefix}{body}.{hash}";
    }
}
