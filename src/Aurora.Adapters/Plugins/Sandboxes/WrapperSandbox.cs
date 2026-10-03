using System.Collections.Concurrent;
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

    public Task<SandboxStart> StartAsync(SandboxLaunch launch, CancellationToken ct) =>
        LauncherThread.Run(() => Start(launch));

    private static SandboxStart Start(SandboxLaunch launch)
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

        IReadOnlyList<string> plugin = PluginCommand.For(launch.Request);

        if (!string.Equals(launch.Plan.FileName, plugin[0], StringComparison.Ordinal))
        {
            // Under a wrapper the plugin's own command goes on the end, after the wrapper's own
            // arguments — that is how sandbox-exec and bwrap both take it. Unconfined, the plan is
            // already that command and adding it again would pass the plugin to itself.
            foreach (var word in plugin)
            {
                start.ArgumentList.Add(word);
            }
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

            return new SandboxStart(new WrapperProcess(process));
        }
        catch (Exception cannotStart)
            when (cannotStart is System.ComponentModel.Win32Exception or IOException)
        {
            return new SandboxStart(null, $"could not start: {cannotStart.GetType().Name}");
        }
    }
}

/// <summary>
/// The one thread every wrapped plugin is started from, and which lives as long as Aurora does.
/// </summary>
/// <remarks>
/// bubblewrap's <c>--die-with-parent</c> is <c>PR_SET_PDEATHSIG</c>, and Linux sends that signal
/// when the <i>thread</i> that created the child exits, not when the process does. The thread
/// pool retires idle threads, so a plugin started from whichever thread ran the call would be
/// tied to that thread's lifetime. Starting every plugin here ties it to Aurora's instead: the
/// plugin dies with Aurora, and with nothing less.
/// </remarks>
internal static class LauncherThread
{
    private static readonly BlockingCollection<Action> Work = new();

    static LauncherThread()
    {
        var thread = new Thread(() =>
        {
            foreach (Action item in Work.GetConsumingEnumerable())
            {
                item();
            }
        })
        {
            // Background, so it never holds the process open; it ends only when the process does.
            IsBackground = true,
            Name = "Aurora plugin launcher",
        };

        thread.Start();
    }

    internal static Task<T> Run<T>(Func<T> start)
    {
        var started = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        Work.Add(() =>
        {
            try
            {
                started.SetResult(start());
            }
            catch (Exception failure)
            {
                started.SetException(failure);
            }
        });

        return started.Task;
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
