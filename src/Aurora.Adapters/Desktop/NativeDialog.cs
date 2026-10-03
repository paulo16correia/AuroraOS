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
        : this(OperatingSystem.IsMacOS() ? Which("osascript")
            : OperatingSystem.IsWindows() ? Which("powershell") ?? Which("pwsh")
            : Which("zenity") ?? Which("kdialog"))
    {
    }

    /// <summary>A dialog driven through a named tool, so what it is asked to show can be checked.</summary>
    internal NativeDialog(string? tool)
    {
        _tool = tool;
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
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in args)
        {
            start.ArgumentList.Add(argument);
        }

        foreach ((var name, var value) in Variables(title, question))
        {
            start.Environment[name] = value;
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

    /// <summary>
    /// What the Windows prompt reads its text from.
    /// </summary>
    /// <remarks>
    /// The PowerShell branch cannot take the question as an argument the way the others do — it is
    /// a script, and text pasted into a script is text that can end it — so it reads two variables
    /// instead. They were never set: the operator got an unlabelled box asking nothing, which is
    /// the failure this class exists to prevent. A prompt whose question is missing is not a
    /// weaker prompt, it is a prompt nobody can answer, and every approval on Windows came back
    /// unanswered because of it.
    /// </remarks>
    internal static IReadOnlyDictionary<string, string> Variables(string title, string question) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AURORA_T"] = title,
            ["AURORA_Q"] = question,
        };

    internal (string File, IReadOnlyList<string> Args) Command(string title, string question, bool secret)
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

            // --entry with --hide-text rather than --password, which takes no --text: the person
            // reads which request it is, and whether it approves or rejects it, before typing a
            // secret.
            "zenity" =>
            (
                _tool!,
                secret
                    ? ["--entry", "--hide-text", "--title", title, "--text", question, "--timeout", "120"]
                    : ["--entry", "--title", title, "--text", question, "--timeout", "120"]
            ),

            "kdialog" => (_tool!, [secret ? "--password" : "--inputbox", question, "--title", title]),

            _ =>
            (
                _tool!,
                [
                    "-NoProfile", "-NonInteractive", "-STA", "-Command",
                    WindowsPrompt.Replace("$SECRET", secret ? "$true" : "$false", StringComparison.Ordinal),
                ]
            ),
        };
    }

    /// <summary>
    /// The Windows prompt: a small form whose field is masked when what is typed is a secret.
    /// </summary>
    /// <remarks>
    /// A form rather than <c>Microsoft.VisualBasic.Interaction.InputBox</c>, which has no masked
    /// mode. The question and the title are read from the environment rather than written into
    /// the script, for the reason
    /// <see cref="Variables"/> gives; the one value substituted is a boolean this class chose.
    /// Cancel or closing the window exits non-zero, which the caller reads as a refusal. Output is
    /// UTF-8 without a byte-order mark, so a passphrase outside ASCII arrives as it was typed.
    /// </remarks>
    internal const string WindowsPrompt =
        """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        [System.Windows.Forms.Application]::EnableVisualStyles()
        $form = New-Object System.Windows.Forms.Form
        $form.Text = $env:AURORA_T
        $form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
        $form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
        $form.MinimizeBox = $false
        $form.MaximizeBox = $false
        $form.TopMost = $true
        $form.ClientSize = New-Object System.Drawing.Size(420, 150)
        $label = New-Object System.Windows.Forms.Label
        $label.Text = $env:AURORA_Q
        $label.SetBounds(12, 12, 396, 48)
        $box = New-Object System.Windows.Forms.TextBox
        $box.UseSystemPasswordChar = $SECRET
        $box.SetBounds(12, 68, 396, 24)
        $ok = New-Object System.Windows.Forms.Button
        $ok.Text = 'OK'
        $ok.DialogResult = [System.Windows.Forms.DialogResult]::OK
        $ok.SetBounds(252, 108, 75, 28)
        $cancel = New-Object System.Windows.Forms.Button
        $cancel.Text = 'Cancel'
        $cancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
        $cancel.SetBounds(333, 108, 75, 28)
        $form.AcceptButton = $ok
        $form.CancelButton = $cancel
        $form.Controls.AddRange(@($label, $box, $ok, $cancel))
        $form.Add_Shown({ $form.Activate(); [void]$box.Focus() })
        if ($form.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { exit 1 }
        [Console]::Out.Write($box.Text)
        exit 0
        """;

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
