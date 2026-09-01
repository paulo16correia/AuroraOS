using System.Diagnostics;
using Aurora.Core.Abstractions;

namespace Aurora.Adapters.Plugins.Sandboxes;

/// <summary>
/// The sandboxes that confine by being in front of the program: <c>sandbox-exec</c>, bubblewrap,
/// and the absence of either.
/// </summary>
/// <remarks>
/// All three start a process the same way, because on those platforms confinement <i>is</i> the
/// command line — the wrapper applies the policy and then becomes the plugin. What differs between
/// them is entirely in <see cref="IPluginSandbox.Plan"/>, which is where it belongs.
/// <para>
/// Split out when the seam grew a way to start processes (docs/adr/0072), so that the platform
/// which cannot express confinement as a command line has somewhere else to do it and these three
/// keep the behaviour they were verified with.
/// </para>
/// </remarks>
public abstract class WrapperSandbox : IPluginSandbox
{
    public abstract SandboxPlan Plan(SandboxRequest request);

    public Task<SandboxStart> StartAsync(SandboxLaunch launch, CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = launch.Plan.FileName,
            WorkingDirectory = launch.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in launch.Plan.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (!string.Equals(launch.Plan.FileName, launch.Executable, StringComparison.Ordinal))
        {
            // Under a wrapper the plugin's own path is the wrapper's last argument. Unconfined,
            // the plan already names the plugin and adding it again would pass it to itself.
            start.ArgumentList.Add(launch.Executable);
        }

        start.Environment.Clear();

        foreach ((var name, var value) in launch.Environment)
        {
            start.Environment[name] = value;
        }

        try
        {
            var process = new Process { StartInfo = start };
            process.Start();

            return Task.FromResult(new SandboxStart(new WrapperProcess(process)));
        }
        catch (Exception cannotStart)
            when (cannotStart is System.ComponentModel.Win32Exception or IOException)
        {
            return Task.FromResult(
                new SandboxStart(null, $"could not start: {cannotStart.GetType().Name}"));
        }
    }
}

/// <summary>A <see cref="Process"/> behind the narrow view a host is given.</summary>
internal sealed class WrapperProcess : ISandboxedProcess
{
    private readonly Process _process;

    internal WrapperProcess(Process process) => _process = process;

    public StreamWriter StandardInput => _process.StandardInput;

    public StreamReader StandardOutput => _process.StandardOutput;

    public StreamReader StandardError => _process.StandardError;

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.ExitCode;

    public Task WaitForExitAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);

    /// <summary>
    /// The whole tree, because a plugin that spawned something has not stopped when it exits.
    /// </summary>
    public void Kill() => _process.Kill(entireProcessTree: true);

    public void Dispose() => _process.Dispose();
}
