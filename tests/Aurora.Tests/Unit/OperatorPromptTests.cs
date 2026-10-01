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
    public void APromptIsAvailableOnThisMachine()
    {
        // Every platform this runs on has one: osascript, zenity/kdialog, or PowerShell. If this
        // is ever false, approvals fall back to a console that a headless deployment does not have.
        Assert.True(new NativeDialog().IsAvailable);
    }
}
