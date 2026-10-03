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
            // An AppContainer, which is a property of the token the process is created with rather
            // than of its command line — so the seam starts the process there (docs/adr/0072).
            //
            // It verifies the child's token before letting it execute an instruction: if anything
            // about the confinement is wrong, the child is terminated and the plugin refused,
            // rather than reported as confined. Run on Windows against a hostile plugin since
            // docs/adr/0078.
            return new Windows.WindowsAppContainerSandbox();
        }

        return new UnconfinedSandbox($"no sandbox is implemented for {RuntimeInformation.OSDescription}");
    }
}
