using System.Diagnostics;
using System.Text;
using Aurora.Core;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Plugins;

/// <summary>
/// Runs a plugin in its own process, over stdin and stdout.
/// </summary>
/// <remarks>
/// What this actually isolates, stated exactly, because RFC 060 rule 2 is the rule most easily
/// claimed and least easily kept:
/// <list type="bullet">
/// <item><b>The process:</b> yes. Separate address space; nothing the plugin does can reach
/// Aurora's objects, and a crash is an exit code rather than a fault in Aurora.</item>
/// <item><b>The database and the vault:</b> yes, and by construction. The child is given the
/// invocation on stdin and nothing else — no connection string, no key path, no handle. The
/// environment is <b>not inherited</b>, so a variable Aurora happens to hold does not travel.</item>
/// <item><b>The filesystem:</b> partly. The working directory is a per-plugin folder and the
/// environment carries no paths, but the child runs as the same OS user and can read what that
/// user can read.</item>
/// <item><b>The network:</b> <b>no.</b> Nothing here prevents a child process opening a socket.
/// The declared endpoints are a statement Aurora holds the plugin to when <i>Aurora</i> makes the
/// call, and they are not a firewall.</item>
/// </list>
/// Closing the last two needs an OS sandbox — a container, a jail, seccomp, App Sandbox — which is
/// per-platform work and is not here. Until it is, a plugin is isolated from <i>Aurora</i> and not
/// from the machine, and that is the honest summary to make an install decision against.
/// </remarks>
public sealed class SubprocessPluginHost : IPluginHost
{
    /// <summary>Where each plugin's working directory lives.</summary>
    private readonly string _root;

    public SubprocessPluginHost(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public async Task<PluginResult> InvokeAsync(
        PluginManifest manifest, PluginInvocation invocation, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(manifest.Executable))
        {
            return new PluginResult(false, null, "no_executable", "the manifest names nothing to run", 0);
        }

        var workingDirectory = Path.Combine(_root, manifest.PluginId);
        Directory.CreateDirectory(workingDirectory);

        var start = new ProcessStartInfo
        {
            FileName = manifest.Executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Nothing of Aurora's travels. A key path or a connection string sitting in the parent's
        // environment is exactly the sort of thing that leaks without anybody deciding to pass it.
        start.Environment.Clear();
        start.Environment["AURORA_PLUGIN_ID"] = manifest.PluginId;
        start.Environment["AURORA_CAPABILITY"] = invocation.CapabilityKey;

        var stopwatch = Stopwatch.StartNew();

        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);

        PluginCapability? capability = manifest.Capabilities
            .FirstOrDefault(c => c.Key == invocation.CapabilityKey);

        timeout.CancelAfter(capability?.Timeout ?? TimeSpan.FromSeconds(30));

        try
        {
            process.Start();

            await process.StandardInput.WriteAsync(invocation.InputJson.AsMemory(), timeout.Token)
                .ConfigureAwait(false);
            process.StandardInput.Close();

            var output = new StringBuilder();
            Task<string> reading = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            output.Append(await reading.ConfigureAwait(false));

            if (process.ExitCode != 0)
            {
                // The child's stderr is deliberately not returned. It is written by the plugin and
                // could carry anything; the exit code is the part Aurora can vouch for.
                _ = await errors.ConfigureAwait(false);

                return new PluginResult(
                    false, null, "nonzero_exit",
                    $"exited {process.ExitCode}", stopwatch.ElapsedMilliseconds);
            }

            return new PluginResult(
                true, output.ToString().Trim(), null, "completed", stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill(process);

            return new PluginResult(
                false, null, "timed_out",
                $"took longer than {capability?.Timeout.TotalSeconds ?? 30:F0}s",
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or IOException)
        {
            return new PluginResult(
                false, null, "could_not_start", failure.GetType().Name, stopwatch.ElapsedMilliseconds);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                // The whole tree: a plugin that spawned something is not stopped by killing the
                // shell it started in.
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception gone) when (gone is InvalidOperationException or NotSupportedException)
        {
            // It exited between the check and the kill, which is the outcome that was wanted.
        }
    }
}
