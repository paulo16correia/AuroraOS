using Aurora.Adapters.Desktop;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// The question the operator is asked actually reaches the window (docs/adr/0011, docs/adr/0050).
/// </summary>
/// <remarks>
/// The dialog itself is drawn by the operating system and is not testable here. What is testable
/// is the part that was wrong: the Windows prompt reads its title and question from two variables,
/// nothing set them, and so every approval on Windows showed an unlabelled box and came back
/// unanswered — a gate that cannot be answered is a gate that is always shut.
/// </remarks>
public sealed class OperatorPromptTests
{
    [Fact]
    public void TheQuestionAndTitleAreCarriedToThePrompt()
    {
        IReadOnlyDictionary<string, string> variables =
            NativeDialog.Variables("Aurora — approval", "Approve request 42? Enter the passphrase.");

        Assert.Equal("Aurora — approval", variables["AURORA_T"]);
        Assert.Equal("Approve request 42? Enter the passphrase.", variables["AURORA_Q"]);
    }

    [Fact]
    public void AQuestionCarryingAQuoteIsStillJustAQuestion()
    {
        // The reason it goes through the environment rather than into the script: text pasted into
        // a script is text that can end it. A quote, a semicolon and a command survive as content.
        const string awkward = "Approve \"rm -rf\"; Write-Host pwned? $(whoami)";

        IReadOnlyDictionary<string, string> variables = NativeDialog.Variables("t", awkward);

        Assert.Equal(awkward, variables["AURORA_Q"]);
    }

    [Fact]
    public void APromptIsAvailableWhereverTheMachineHasOne()
    {
        // macOS always has osascript and Windows always has PowerShell. A Linux machine has a
        // prompt only with a desktop — zenity or kdialog — and a headless one has neither, which is
        // a real deployment rather than a broken one: the passphrase is then supplied with the
        // call, or the decision made in the panel.
        var expected = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
            || OnPath("zenity") || OnPath("kdialog");

        Assert.Equal(expected, new NativeDialog().IsAvailable);
    }

    [Theory]
    [InlineData("/usr/bin/zenity")]
    [InlineData("/usr/bin/kdialog")]
    [InlineData("/usr/bin/osascript")]
    public void ThePassphrasePromptSaysWhatIsBeingDecided(string tool)
    {
        // The person reads which request it is, and whether typing the secret approves or rejects
        // it, on every desktop. zenity's --password takes no text, which is why it is not used.
        (_, IReadOnlyList<string> args) = new NativeDialog(tool)
            .Command("Aurora — approval", "Reject request 42? Enter the operator passphrase.", secret: true);

        Assert.Contains("Reject request 42? Enter the operator passphrase.", args);
    }

    [Fact]
    public void TheWindowsPromptMasksASecretAndCarriesNoTextOfItsOwn()
    {
        const string question = "Approve request 42? Enter the operator passphrase.";
        var dialog = new NativeDialog("powershell.exe");

        (_, IReadOnlyList<string> secret) = dialog.Command("Aurora — approval", question, secret: true);
        (_, IReadOnlyList<string> plain) = dialog.Command("Aurora — approval", question, secret: false);

        // A secret is masked as it is typed; an ordinary answer is not.
        Assert.Contains("UseSystemPasswordChar = $true", secret[^1], StringComparison.Ordinal);
        Assert.Contains("UseSystemPasswordChar = $false", plain[^1], StringComparison.Ordinal);

        // Forms need a single-threaded apartment, and the question reaches the window through the
        // environment rather than through the script, where text could end it.
        Assert.Contains("-STA", secret);
        Assert.DoesNotContain(question, secret[^1], StringComparison.Ordinal);
        Assert.Contains("$env:AURORA_Q", secret[^1], StringComparison.Ordinal);
    }

    private static bool OnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(Path.Combine(directory, tool)));
}
