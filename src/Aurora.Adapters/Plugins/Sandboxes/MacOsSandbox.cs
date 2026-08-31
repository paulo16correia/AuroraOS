using System.Text;
using Aurora.Core.Abstractions;

namespace Aurora.Adapters.Plugins.Sandboxes;

/// <summary>
/// Confines a plugin with the macOS sandbox, through <c>sandbox-exec</c>.
/// </summary>
/// <remarks>
/// The profile is deny-by-default and then opens exactly three things: reading the system (an
/// interpreter has to find its own runtime), reading the plugin's own installed directory, and
/// reading and writing the plugin's working directory. Network is denied outright, and the owner's
/// home is denied for reading — which is where Aurora's database, its key files and everything
/// personal live.
/// <para>
/// <c>sandbox-exec</c> is marked deprecated in its own manual page and has been for years. It is
/// also present on every macOS, is the interface App Sandbox itself is built on, and is the only
/// way to confine a child process without shipping a signed helper with entitlements. The
/// alternative on offer was no confinement at all, so it is used, and the fact that Apple may one
/// day remove it is recorded rather than discovered later: if it disappears,
/// <see cref="PluginSandbox.ForThisMachine"/> finds no binary and Aurora refuses instead of
/// silently running plugins loose.
/// </para>
/// </remarks>
public sealed class MacOsSandbox : IPluginSandbox
{
    internal const string SandboxExec = "/usr/bin/sandbox-exec";

    public SandboxPlan Plan(SandboxRequest request)
    {
        // Resolved, not merely absolute: the sandbox matches the path the kernel reaches, and
        // the system temporary directory is reached through a symlink (see SandboxPaths).
        var workingDirectory = SandboxPaths.Real(request.WorkingDirectory);
        var executable = SandboxPaths.Real(request.Executable);
        var installed = Path.GetDirectoryName(executable) ?? workingDirectory;

        return new SandboxPlan(
            SandboxExec,
            ["-p", Profile(workingDirectory, installed, request.NetworkGranted, request.GpuGranted)],
            SandboxLevel.Confined,
            "sandbox-exec",
            []);
    }

    /// <summary>
    /// Builds the profile. Order matters: in SBPL the last rule that matches wins, so every
    /// exception has to come after the denial it is an exception to.
    /// </summary>
    private static string Profile(
        string workingDirectory, string installed, bool network, bool gpu)
    {
        var profile = new StringBuilder();
        profile.Append("(version 1)");
        profile.Append("(deny default)");

        // An interpreter forks, execs, reads sysctls and talks to launchd to start at all. None of
        // these grant reach outside the policy below; refusing them just means nothing runs.
        profile.Append("(allow process*)");
        profile.Append("(allow sysctl-read)");
        profile.Append("(allow mach*)");
        profile.Append("(allow signal (target self))");
        profile.Append("(allow ipc-posix-shm)");

        // Read the system, so a Python or a Node can find itself...
        profile.Append("(allow file-read*)");

        // ...but not the owner's files. Aurora's database, its four key files, and everything the
        // owner has ever written are all under one of these two roots.
        profile.Append("(deny file-read* (subpath \"/Users\") (subpath \"/private/var/root\"))");

        // Except the plugin's own directory, which it must be able to read to be executed at all.
        profile.Append($"(allow file-read* {Subpath(installed)})");

        // And its working directory, the one place it may leave anything behind.
        profile.Append($"(allow file-read* file-write* {Subpath(workingDirectory)})");

        // stdout and stderr are pipes Aurora holds; writing to them is how a plugin answers.
        profile.Append(
            "(allow file-write-data (literal \"/dev/null\") (literal \"/dev/stdout\") "
            + "(literal \"/dev/stderr\") (literal \"/dev/dtracehelper\"))");

        // The rule this whole class exists for.
        profile.Append("(deny network*)");

        // Unless the owner granted it, once, naming the hosts (docs/adr/0067). SBPL filters by
        // socket and port, not by name — there is no rule here that says "discord.com". So this is
        // the honest shape of the grant: outbound TCP, and the host list is what the owner agreed
        // to and what the audit records, not something the kernel checks.
        if (network)
        {
            profile.Append("(allow network-outbound (remote tcp))");
            profile.Append("(allow network-outbound (remote udp))");
            profile.Append("(allow system-socket)");

            // And the resolver, which on macOS is neither TCP nor UDP: name lookups go to
            // mDNSResponder over a unix socket. Without this a plugin granted the network can
            // open a connection to an address and cannot find out what any address is, which
            // surfaces as gaierror and looks like the network being down rather than a rule
            // being missing.
            //
            // Named as one literal rather than allowing unix sockets generally, so this grants
            // the plugin name resolution and not a channel to every other daemon on the machine.
            profile.Append("(allow network-outbound (literal \"/private/var/run/mDNSResponder\"))");

            // And the answers. Outbound covers sending; a reply arriving on a socket this plugin
            // opened is inbound, and denying it means a plugin can send a request and never hear
            // the response — which surfaces as EPERM from recvfrom and reads like the socket being
            // broken rather than a rule being missing.
            //
            // Narrowed to UDP on this process's own sockets: enough for a request-and-reply
            // protocol, and not a licence to listen for connections. A plugin still cannot accept
            // anything.
            profile.Append("(allow network-inbound (local udp))");
        }

        if (gpu)
        {
            // Two rules, found by measuring rather than by reading: without the first, Metal loads
            // its backend and the process dies without a word — the segmentation fault that reads
            // as a broken recogniser. Without the second it initialises, reports the GPU by name,
            // and quietly computes on the processor instead, which is the same answer twenty times
            // slower and with nothing to say why.
            profile.Append("(allow iokit-open)");

            // The shader cache, and only that. Metal compiles pipelines on first use and writes
            // them somewhere under the user's cache directory; denying the write does not fail,
            // it falls back. Matched by name rather than by allowing the directory, so this grants
            // a plugin a shader cache and not the rest of what lives beside it.
            profile.Append("(allow file-write* (regex #\"/com\\.apple\\.metal\"))");
        }

        return profile.ToString();
    }

    /// <summary>
    /// Quotes a path for SBPL. A path containing a quote or a backslash would otherwise end the
    /// string early and turn the rest of the directory name into policy.
    /// </summary>
    private static string Subpath(string path)
    {
        var escaped = path.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

        return $"(subpath \"{escaped}\")";
    }
}
