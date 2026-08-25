using System.Diagnostics;
using System.Text;
using Aurora.Core.Abstractions;

namespace Aurora.Adapters.Desktop;

/// <summary>
/// Asks the person, in a window the operating system draws.
/// </summary>
/// <remarks>
/// The point is who renders it. A prompt the agent composes is a prompt the agent can lie in; this
/// one is drawn by the OS from arguments Aurora passed, in a process the agent cannot reach. The
/// dialog is not signed and not tamper-proof against a local attacker who already runs code as this
/// user — nothing achievable here is — but it closes the gap that mattered: <b>the agent cannot
/// spoof the question or read the answer.</b>
/// <para>
/// Per platform: <c>osascript</c> on macOS, <c>zenity</c> or <c>kdialog</c> on Linux, and a
/// PowerShell prompt on Windows. Where none is available, <see cref="IsAvailable"/> is false and
/// the caller falls back to the console rather than pretending it asked.
/// </para>
/// </remarks>
public sealed class NativeDialog : IOperatorPrompt
{
    private readonly string? _tool;

    public NativeDialog()
    {
        _tool = OperatingSystem.IsMacOS() ? Which("osascript")
            : OperatingSystem.IsWindows() ? Which("powershell") ?? Which("pwsh")
            : Which("zenity") ?? Which("kdialog");
    }

    public bool IsAvailable => _tool is not null;

    public async Task<OperatorAnswer> AskAsync(
        string title, string question, bool secret, TimeSpan timeout, CancellationToken ct)
    {
        if (_tool is null)
        {
            return new OperatorAnswer(false, null, "no desktop prompt is available on this machine");
        }

        // Everything shown is passed as an argument, never interpolated into a script. A question
        // carrying a quote would otherwise be a question carrying a command.
        (var file, var args) = Command(title, question, secret);

        var start = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in args)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(timeout);

        try
        {
            process.Start();

            var answer = await process.StandardOutput.ReadToEndAsync(window.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(window.Token).ConfigureAwait(false);

            // A dismissed dialog is a refusal, not an absence of one. Treating "they closed it" as
            // "ask again later" is how a prompt becomes something people click through.
            return process.ExitCode != 0
                ? new OperatorAnswer(false, null, "dismissed")
                : new OperatorAnswer(true, answer.TrimEnd('\n', '\r'), "answered");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill(process);
            return new OperatorAnswer(false, null, "nobody answered in time");
        }
        catch (Exception unavailable)
            when (unavailable is System.ComponentModel.Win32Exception or IOException)
        {
            return new OperatorAnswer(false, null, unavailable.GetType().Name);
        }
    }

    public async Task NotifyAsync(string title, string message, CancellationToken ct)
    {
        if (_tool is null)
        {
            return;
        }

        (var file, var args) = Notification(title, message);

        var start = new ProcessStartInfo
        {
            FileName = file, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        };

        foreach (var argument in args)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is not null)
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception unavailable)
            when (unavailable is System.ComponentModel.Win32Exception or IOException)
        {
            // A notification that could not be shown is not a reason to fail the thing it was
            // about. The signal it describes is on the bus either way.
        }
    }

    private (string File, IReadOnlyList<string> Args) Command(string title, string question, bool secret)
    {
        var name = Path.GetFileName(_tool!);

        return name switch
        {
            "osascript" =>
            (
                _tool!,
                [
                    "-e",
                    "on run {t, q, h}\n"
                    + "  set r to display dialog q with title t default answer \"\" "
                    + "    hidden answer (h as boolean) buttons {\"Cancel\", \"OK\"} default button \"OK\"\n"
                    + "  return text returned of r\n"
                    + "end run",
                    title, question, secret ? "true" : "false",
                ]
            ),

            "zenity" =>
            (
                _tool!,
                secret
                    ? ["--password", "--title", title, "--timeout", "120"]
                    : ["--entry", "--title", title, "--text", question, "--timeout", "120"]
            ),

            "kdialog" => (_tool!, [secret ? "--password" : "--inputbox", question, "--title", title]),

            _ =>
            (
                _tool!,
                [
                    "-NoProfile", "-NonInteractive", "-Command",
                    "Add-Type -AssemblyName Microsoft.VisualBasic;"
                    + "[Microsoft.VisualBasic.Interaction]::InputBox($env:AURORA_Q,$env:AURORA_T)",
                ]
            ),
        };
    }

    private (string File, IReadOnlyList<string> Args) Notification(string title, string message)
    {
        var name = Path.GetFileName(_tool!);

        return name switch
        {
            "osascript" =>
            (
                _tool!,
                [
                    "-e",
                    "on run {t, m}\n  display notification m with title t\nend run",
                    title, message,
                ]
            ),

            "zenity" => (_tool!, ["--notification", "--text", $"{title}: {message}"]),
            "kdialog" => (_tool!, ["--passivepopup", $"{title}: {message}", "10"]),
            _ => (_tool!, ["-NoProfile", "-NonInteractive", "-Command", "exit 0"]),
        };
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception gone) when (gone is InvalidOperationException or NotSupportedException)
        {
        }
    }

    /// <summary>Finds a tool on PATH, without a shell.</summary>
    private static string? Which(string tool)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        var names = OperatingSystem.IsWindows() ? new[] { tool + ".exe", tool } : [tool];

        foreach (var directory in paths)
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(directory, name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is not a reason to stop looking at the rest.
                }
            }
        }

        return null;
    }
}
