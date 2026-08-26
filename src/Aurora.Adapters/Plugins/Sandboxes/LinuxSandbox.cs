using Aurora.Core.Abstractions;

namespace Aurora.Adapters.Plugins.Sandboxes;

/// <summary>
/// Confines a plugin with bubblewrap: a new mount namespace, a new network namespace with nothing
/// in it, and a filesystem that is read-only everywhere except one directory.
/// </summary>
/// <remarks>
/// <c>bwrap</c> rather than <c>unshare</c>, seccomp or a container runtime, because it is the one
/// tool that needs no root, no daemon and no image, and because it is what Flatpak uses — so on a
/// desktop Linux it is usually already installed. When it is not,
/// <see cref="PluginSandbox.ForThisMachine"/> returns <see cref="UnconfinedSandbox"/> naming it,
/// and the owner can install it or accept running unconfined. Aurora does not pretend either way.
/// <para>
/// <b>This has not been run.</b> The machine Aurora was built on is a Mac; the flags below are
/// bubblewrap's documented interface and the profile mirrors <see cref="MacOsSandbox"/> exactly,
/// but the first person to run a plugin on Linux is running this code for the first time. That is
/// stated here rather than left for them to find out.
/// </para>
/// </remarks>
public sealed class LinuxSandbox : IPluginSandbox
{
    private readonly string _bwrap;

    public LinuxSandbox(string bwrap)
    {
        _bwrap = bwrap;
    }

    /// <summary>The usual two locations, checked directly rather than by searching PATH.</summary>
    /// <remarks>
    /// PATH is inherited from whoever started Aurora and can name a directory anybody can write
    /// to. A sandbox found that way could be a program that pretends to sandbox.
    /// </remarks>
    internal static string? Find()
    {
        foreach (var candidate in new[] { "/usr/bin/bwrap", "/bin/bwrap" })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public SandboxPlan Plan(SandboxRequest request)
    {
        var workingDirectory = SandboxPaths.Real(request.WorkingDirectory);
        var executable = SandboxPaths.Real(request.Executable);
        var installed = Path.GetDirectoryName(executable) ?? workingDirectory;

        List<string> arguments =
        [
            // No network namespace means no route to anywhere, not a filtered one.
            "--unshare-net",

            // Its own PID namespace, so it cannot see or signal Aurora or anything else.
            "--unshare-pid",
            "--unshare-uts",
            "--unshare-ipc",

            // If Aurora dies, the plugin dies. A timeout that leaves an orphan is not a timeout.
            "--die-with-parent",

            // No path to privilege, whatever the binary's mode bits say.
            "--new-session",
            "--cap-drop", "ALL",

            // The system, read-only: the interpreter has to find its own runtime.
            "--ro-bind", "/usr", "/usr",
            "--ro-bind-try", "/lib", "/lib",
            "--ro-bind-try", "/lib64", "/lib64",
            "--ro-bind-try", "/bin", "/bin",
            "--ro-bind-try", "/sbin", "/sbin",
            "--ro-bind-try", "/etc/alternatives", "/etc/alternatives",
            "--ro-bind-try", "/etc/ssl/certs", "/etc/ssl/certs",
            "--proc", "/proc",
            "--dev", "/dev",
            "--tmpfs", "/tmp",

            // The plugin's own files, read-only, and its working directory, writable. Nothing
            // else of the owner's home is mounted at all, so there is nothing to deny.
            "--ro-bind", installed, installed,
            "--bind", workingDirectory, workingDirectory,
            "--chdir", workingDirectory,
            "--",
        ];

        return new SandboxPlan(_bwrap, arguments, SandboxLevel.Confined, "bubblewrap", []);
    }
}
