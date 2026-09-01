using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Aurora.Core.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace Aurora.Adapters.Plugins.Sandboxes.Windows;

/// <summary>
/// A confined process, seen through the pipes Aurora kept and the handle it holds.
/// </summary>
/// <remarks>
/// Not a <see cref="System.Diagnostics.Process"/>: that class can only describe a process it
/// started itself, and this one was created by <c>CreateProcess</c> with an attribute list it does
/// not know about. What it gives up in convenience it gains in not being able to start anything.
/// <para>
/// The job handle is the reason a plugin cannot outlive Aurora. Closing it kills everything
/// assigned to it, so a crash that never reaches <see cref="Dispose"/> still takes the plugin with
/// it when the process ends and its handles close — which is what bubblewrap's
/// <c>--die-with-parent</c> buys on Linux.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class AppContainerProcess : ISandboxedProcess
{
    private readonly IntPtr _process;
    private readonly AnonymousPipes _pipes;

    private IntPtr _job;
    private int _exitCode;
    private bool _disposed;

    internal AppContainerProcess(IntPtr process, IntPtr job, AnonymousPipes pipes)
    {
        _process = process;
        _job = job;
        _pipes = pipes;

        // Aurora's copies of the child's ends go now. Holding them would mean the pipe never
        // reports end-of-file when the plugin exits, and every read would hang instead.
        pipes.CloseChildEnds();

        StandardInput = new StreamWriter(
            new FileStream(pipes.ParentInput, FileAccess.Write), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };

        StandardOutput = new StreamReader(
            new FileStream(pipes.ParentOutput, FileAccess.Read), Encoding.UTF8);

        StandardError = new StreamReader(
            new FileStream(pipes.ParentError, FileAccess.Read), Encoding.UTF8);
    }

    public StreamWriter StandardInput { get; }

    public StreamReader StandardOutput { get; }

    public StreamReader StandardError { get; }

    public bool HasExited
    {
        get
        {
            if (!Win32.GetExitCodeProcess(_process, out var code))
            {
                // Unreadable is treated as gone. The alternative is a host that waits forever on
                // something it can no longer ask about.
                return true;
            }

            // Windows' long-standing ambiguity: a process still running and a process that chose
            // to exit with 259 are indistinguishable through this call. Read the way that keeps a
            // host waiting rather than the way that declares a running plugin dead — a plugin that
            // exits with 259 is noticed when its pipes close, and one wrongly called dead would be
            // restarted while the first copy was still holding its connection.
            const uint StillActive = 259;

            if (code == StillActive)
            {
                return false;
            }

            _exitCode = (int)code;
            return true;
        }
    }

    public int ExitCode => _exitCode;

    /// <summary>
    /// Waits by polling, because there is no managed handle to await on.
    /// </summary>
    /// <remarks>
    /// A quarter of a second. The alternative is a <c>WaitForSingleObject</c> on a thread pool
    /// thread held for the plugin's whole life, and a plugin that runs for an hour would hold it
    /// for an hour.
    /// </remarks>
    public async Task WaitForExitAsync(CancellationToken ct)
    {
        while (!HasExited)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        }
    }

    public void Kill()
    {
        // The job, not the process. A plugin that spawned something is not stopped by killing the
        // one process Aurora knows the handle of, and everything it spawned is in the job.
        //
        // The handle is cleared as it is closed. Killing and then disposing is the ordinary
        // sequence, and closing the same handle twice is not a harmless mistake on Windows: the
        // number may by then belong to something else entirely.
        if (Interlocked.Exchange(ref _job, IntPtr.Zero) is var job && job != IntPtr.Zero)
        {
            Win32.CloseHandle(job);
            return;
        }

        Win32.TerminateProcess(_process, 1);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        StandardInput.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
        _pipes.Dispose();

        if (Interlocked.Exchange(ref _job, IntPtr.Zero) is var job && job != IntPtr.Zero)
        {
            Win32.CloseHandle(job);
        }

        Win32.CloseHandle(_process);
    }
}
