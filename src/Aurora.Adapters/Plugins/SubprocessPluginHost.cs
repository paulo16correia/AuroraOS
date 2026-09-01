using System.Diagnostics;
using System.Text;
using Aurora.Core;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Adapters.Plugins.Sandboxes;

namespace Aurora.Adapters.Plugins;

/// <summary>
/// Runs a plugin in its own process, over stdin and stdout, confined by the operating system.
/// </summary>
/// <remarks>
/// What this isolates, stated exactly, because RFC 060 rule 2 is the rule most easily claimed and
/// least easily kept:
/// <list type="bullet">
/// <item><b>The process:</b> yes, always. Separate address space; nothing the plugin does can
/// reach Aurora's objects, and a crash is an exit code rather than a fault in Aurora.</item>
/// <item><b>The database and the vault:</b> yes, always, and by construction. The child is given
/// the invocation on stdin and nothing else — no connection string, no key path, no handle. The
/// environment is <b>not inherited</b>, so a variable Aurora happens to hold does not travel.</item>
/// <item><b>The filesystem and the network:</b> only as far as the <see cref="IPluginSandbox"/>
/// reaches. Where the platform has one, the plugin cannot open a socket, cannot read the owner's
/// home, and can write only to its own working directory. Where it does not, none of that is true
/// — and this host <b>refuses to invoke</b> rather than run a third party's code loose, unless the
/// owner has said otherwise.</item>
/// </list>
/// The refusal is the point. Before this, an unconfined plugin and a confined one produced
/// identical results, so the missing half of rule 2 was invisible at the only moment it mattered.
/// </remarks>
public sealed class SubprocessPluginHost : IPluginHost
{
    /// <summary>Where each plugin's working directory lives.</summary>
    private readonly string _root;

    private readonly IPluginSandbox _sandbox;

    /// <summary>
    /// Whether the owner has accepted running plugins the platform cannot confine.
    /// </summary>
    private readonly bool _allowUnconfined;

    /// <param name="root">The directory under which each plugin gets a working directory.</param>
    /// <param name="sandbox">
    /// The confinement to apply. Defaults to the strongest this machine can deliver.
    /// </param>
    /// <param name="allowUnconfined">
    /// Set only by an owner who has read what it costs. Default <see langword="false"/>: a
    /// platform Aurora cannot confine gets a refusal, not a quiet exception to rule 2.
    /// </param>
    public SubprocessPluginHost(
        string root, IPluginSandbox? sandbox = null, bool allowUnconfined = false)
    {
        _root = Path.GetFullPath(root);
        _sandbox = sandbox ?? PluginSandbox.ForThisMachine();
        _allowUnconfined = allowUnconfined;
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// What plugins on this machine actually run under, for the panel and the install decision.
    /// </summary>
    public SandboxPlan Confinement => _sandbox.Plan(
        new SandboxRequest("aurora", Path.Combine(_root, "plugin"), _root));

    public async Task<PluginResult> InvokeAsync(
        PluginManifest manifest, PluginInvocation invocation, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(manifest.Executable))
        {
            return new PluginResult(false, null, "no_executable", "the manifest names nothing to run", 0);
        }

        var workingDirectory = Path.Combine(_root, manifest.PluginId);
        Directory.CreateDirectory(workingDirectory);

        var request = new SandboxRequest(
            manifest.PluginId, manifest.Executable, workingDirectory, invocation.NetworkGranted);

        SandboxPlan plan = _sandbox.Plan(request);

        if (plan.Level == SandboxLevel.Process && !_allowUnconfined)
        {
            // Named in full, because the owner has to decide, and can only decide against a
            // description of what they would be accepting.
            var cost = string.Join("; ", plan.Unenforced);

            return new PluginResult(
                false, null, PluginRefusal.SandboxUnavailable,
                $"{manifest.PluginId} was not run: {plan.Mechanism}. Unconfined, {cost}. "
                + "Set Aurora:Plugins:AllowUnconfined to accept that.",
                0);
        }

        // Nothing of Aurora's travels. A key path or a connection string sitting in the parent's
        // environment is exactly the sort of thing that leaks without anybody deciding to pass it.
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AURORA_PLUGIN_ID"] = manifest.PluginId,
            ["AURORA_CAPABILITY"] = invocation.CapabilityKey,

            // A fixed PATH, not an inherited one. The property that matters is that nothing of
            // Aurora's travels, and a constant naming only system directories carries nothing —
            // while without it a script beginning "#!/usr/bin/env python3" cannot find an
            // interpreter and every plugin written the ordinary way fails with exit 127.
            ["PATH"] = OperatingSystem.IsWindows()
                ? @"C:\Windows\System32"
                : "/usr/bin:/bin:/usr/local/bin",
        };

        var stopwatch = Stopwatch.StartNew();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);

        PluginCapability? capability = manifest.Capabilities
            .FirstOrDefault(c => c.Key == invocation.CapabilityKey);

        timeout.CancelAfter(capability?.Timeout ?? TimeSpan.FromSeconds(30));

        // The sandbox starts it. On one platform confinement is a property of the token a process
        // is created with rather than of its command line (docs/adr/0072), and a sandbox that
        // could not prove what it promised refuses here rather than handing back a process.
        SandboxStart started = await _sandbox
            .StartAsync(
                new SandboxLaunch(
                    // The plugin's own directory, which under a sandbox is also the only one it
                    // may write to. Losing this makes the child inherit Aurora's directory
                    // instead, and every relative path it uses lands somewhere correctly refused.
                    request, plan, manifest.Executable, workingDirectory, environment),
                timeout.Token)
            .ConfigureAwait(false);

        if (started.Process is not { } process)
        {
            return new PluginResult(
                false, null, PluginRefusal.SandboxUnavailable,
                started.Refused ?? "the sandbox did not start it and did not say why",
                stopwatch.ElapsedMilliseconds);
        }

        using (process)
        {
            return await ExchangeAsync(
                process, manifest, invocation, capability, stopwatch, timeout, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One request in, one answer out, over the pipes of a process somebody else confined.
    /// </summary>
    private static async Task<PluginResult> ExchangeAsync(
        ISandboxedProcess process, PluginManifest manifest, PluginInvocation invocation,
        PluginCapability? capability, Stopwatch stopwatch,
        CancellationTokenSource timeout, CancellationToken ct)
    {
        try
        {
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

    private static void Kill(ISandboxedProcess process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (Exception gone) when (gone is InvalidOperationException or NotSupportedException)
        {
            // It exited between the check and the kill, which is the outcome that was wanted.
        }
    }
}
