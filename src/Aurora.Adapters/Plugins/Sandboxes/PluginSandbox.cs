using System.Runtime.InteropServices;
using Aurora.Core.Abstractions;

namespace Aurora.Adapters.Plugins.Sandboxes;

/// <summary>
/// Picks the confinement this machine can actually deliver.
/// </summary>
/// <remarks>
/// Chosen once, at startup, by asking the operating system what it is and then asking the
/// filesystem whether the tool is there. Not per invocation: a plugin's confinement should not
/// depend on whether somebody installed something between two calls.
/// </remarks>
public static class PluginSandbox
{
    /// <summary>
    /// The strongest sandbox available here, or <see cref="UnconfinedSandbox"/> with the reason.
    /// </summary>
    public static IPluginSandbox ForThisMachine()
    {
        if (OperatingSystem.IsMacOS())
        {
            return File.Exists(MacOsSandbox.SandboxExec)
                ? new MacOsSandbox()
                : new UnconfinedSandbox($"{MacOsSandbox.SandboxExec} is not on this system");
        }

        if (OperatingSystem.IsLinux())
        {
            string? bwrap = LinuxSandbox.Find();

            return bwrap is not null
                ? new LinuxSandbox(bwrap)
                : new UnconfinedSandbox(
                    "bubblewrap (bwrap) is not installed; it is the only user-namespace sandbox "
                    + "Aurora can drive without root");
        }

        if (OperatingSystem.IsWindows())
        {
            // AppContainer would do it, and it is reached through CreateProcessAsUser with a
            // capability SID — not through ProcessStartInfo. Claiming a Windows sandbox that has
            // never run on Windows would be worse than saying there is none.
            return new UnconfinedSandbox(
                "Windows confinement needs an AppContainer token, which Aurora does not yet create");
        }

        return new UnconfinedSandbox($"no sandbox is implemented for {RuntimeInformation.OSDescription}");
    }
}
