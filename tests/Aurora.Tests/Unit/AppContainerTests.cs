using System.Runtime.Versioning;
using Aurora.Adapters.Plugins.Sandboxes.Windows;
using Aurora.Core.Abstractions;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Windows plugin confinement, in the parts that can be judged from anywhere (docs/adr/0072).
/// </summary>
/// <remarks>
/// <b>What these prove, and what they cannot.</b> The AppContainer implementation is split so that
/// everything deciding how much authority a plugin gets — which capabilities, which paths, and
/// whether a created process is allowed to run — is pure and tested here, on whatever machine the
/// suite runs on. What is left in the interop is the asking: create the profile, create the
/// process, read the token.
/// <para>
/// So a green run of this file means Aurora's <i>decisions</i> about confinement are right. It
/// does not mean the Windows kernel honours them, and nothing run on a Mac ever could. That is
/// recorded as UNVERIFIED in <c>docs/reference/platform-support.md</c> and stays there until the
/// suite has run on Windows.
/// </para>
/// </remarks>
public sealed class AppContainerTests
{
    /// <summary>
    /// Where the program lives and where it may write, built for whichever machine is running.
    /// </summary>
    /// <remarks>
    /// Not Windows path literals, though this is Windows code. <c>Path.GetFullPath</c> reads
    /// <c>C:\plugins</c> on a Mac as one long relative file name, so a test written with them
    /// would compare two paths that both had the runner's working directory glued to the front and
    /// would pass or fail for reasons that have nothing to do with the profile.
    /// </remarks>
    private static readonly string Installed =
        Path.Combine(Path.GetTempPath(), "aurora-appcontainer", "installed", "acme");

    private static readonly string Working =
        Path.Combine(Path.GetTempPath(), "aurora-appcontainer", "working", "acme");

    private static SandboxRequest Request(
        bool network = false, bool gpu = false,
        string? executable = null, string? working = null) =>
        new("acme/notes",
            executable ?? Path.Combine(Installed, "run.exe"),
            working ?? Working,
            network,
            gpu);

    // ---- the verdict: every way a created process can fail to be confined ----

    [Fact]
    public void ATokenThatCannotBeReadIsRefused()
    {
        AppContainerVerdict verdict = AppContainerVerdict.Of(
            tokenRead: false, isAppContainer: false, actualSid: null, expectedSid: "S-1-15-2-1");

        // Not "probably fine". A confinement that could not be checked has not been demonstrated,
        // and this is the branch where an optimistic reading would let an unconfined plugin run.
        Assert.False(verdict.Confined);
        Assert.Contains("could not be demonstrated", verdict.Refused);
    }

    [Fact]
    public void AProcessCreatedOutsideAnAppContainerIsRefused()
    {
        AppContainerVerdict verdict = AppContainerVerdict.Of(
            tokenRead: true, isAppContainer: false, actualSid: null, expectedSid: "S-1-15-2-1");

        // The failure the whole design exists to catch: a CreateProcess that ignored the
        // security-capabilities attribute produces a working plugin with nothing constraining it,
        // and every layer above would have called it confined.
        Assert.False(verdict.Confined);
        Assert.Contains("outside an AppContainer", verdict.Refused);
    }

    [Fact]
    public void AnAppContainerThatWillNotNameItselfIsRefused()
    {
        AppContainerVerdict verdict = AppContainerVerdict.Of(
            tokenRead: true, isAppContainer: true, actualSid: null, expectedSid: "S-1-15-2-1");

        Assert.False(verdict.Confined);
        Assert.Contains("does not identify itself", verdict.Refused);
    }

    [Fact]
    public void AProcessInSomebodyElsesContainerIsRefused()
    {
        AppContainerVerdict verdict = AppContainerVerdict.Of(
            tokenRead: true, isAppContainer: true,
            actualSid: "S-1-15-2-9999", expectedSid: "S-1-15-2-1");

        // A different container is a different set of grants — possibly a wider one, and certainly
        // not the one whose filesystem access Aurora just decided.
        Assert.False(verdict.Confined);
        Assert.Contains("different AppContainer", verdict.Refused);
    }

    [Fact]
    public void OnlyTheRightContainerIsAllowedToRun()
    {
        AppContainerVerdict verdict = AppContainerVerdict.Of(
            tokenRead: true, isAppContainer: true,
            actualSid: "S-1-15-2-1", expectedSid: "S-1-15-2-1");

        Assert.True(verdict.Confined);
        Assert.Null(verdict.Refused);
    }

    [Fact]
    public void TheContainerIsMatchedWithoutRegardToCase()
    {
        // Windows writes SIDs in either case depending on which API produced them, and refusing a
        // correctly confined plugin over that would be a fail-closed that fails on everything.
        Assert.True(AppContainerVerdict
            .Of(true, true, "s-1-15-2-1", "S-1-15-2-1").Confined);
    }

    // ---- the profile: how much authority a plugin is given ----

    [Fact]
    public void APluginWithoutTheNetworkGetsNoCapabilities()
    {
        AppContainerProfile profile = AppContainerProfiles.For(Request());

        // An AppContainer starts with none, and this is what keeps it that way.
        Assert.Empty(profile.Capabilities);
    }

    [Fact]
    public void APluginGrantedTheNetworkGetsExactlyOneCapability()
    {
        AppContainerProfile profile = AppContainerProfiles.For(Request(network: true));

        Assert.Equal([AppContainerCapability.InternetClient], profile.Capabilities);
    }

    [Fact]
    public void TheGraphicsProcessorGrantsNothingHereAndSaysNothingFalse()
    {
        AppContainerProfile profile = AppContainerProfiles.For(Request(gpu: true));

        // macOS grants the GPU by opening the IOKit surface. Windows has no capability that means
        // the same thing, so a plugin that asked for it gets a container without it and finds out
        // from its own failure — which is the honest outcome until an equivalent is implemented
        // and verified, rather than a capability added because the request had a flag.
        Assert.Empty(profile.Capabilities);
    }

    [Fact]
    public void ThePluginMayWriteItsOwnDirectoryAndReadWhereItsProgramLives()
    {
        AppContainerProfile profile = AppContainerProfiles.For(Request());

        AppContainerGrant working = Assert.Single(
            profile.Grants, g => g.Access == AppContainerAccess.Full);
        AppContainerGrant program = Assert.Single(
            profile.Grants, g => g.Access == AppContainerAccess.ReadExecute);

        Assert.Equal(Path.GetFullPath(Working), working.Path);
        Assert.Equal(Path.GetFullPath(Installed), program.Path);

        // Exactly two. An AppContainer reaches nothing it was not named on, so this list is the
        // whole of the plugin's filesystem, and a third entry would be a third thing it can see.
        Assert.Equal(2, profile.Grants.Count);
    }

    [Fact]
    public void TheProgramDirectoryIsNeverGrantedWritable()
    {
        AppContainerProfile profile = AppContainerProfiles.For(Request());

        AppContainerGrant program = Assert.Single(
            profile.Grants, g => g.Access == AppContainerAccess.ReadExecute);

        // With more than read-and-execute a plugin could rewrite its own installed code, and the
        // manifest hash would describe something that no longer runs.
        Assert.NotEqual(AppContainerAccess.Full, program.Access);
    }

    [Fact]
    public void APluginRunningFromItsOwnDirectoryIsGrantedItOnce()
    {
        AppContainerProfile profile = AppContainerProfiles.For(
            Request(executable: Path.Combine(Working, "run.exe"), working: Working));

        // Two rules over one path, one of them read-only, is an ACL nobody can reason about. The
        // writable one is the plugin's own directory, so that is the one that stands.
        AppContainerGrant only = Assert.Single(profile.Grants);
        Assert.Equal(AppContainerAccess.Full, only.Access);
    }

    // ---- the container's name ----

    [Fact]
    public void TheContainerNameIsSomethingWindowsAccepts()
    {
        var name = AppContainerProfiles.NameFor("acme/notes");

        Assert.True(name.Length <= 64, $"'{name}' is {name.Length} characters; Windows allows 64");
        Assert.All(
            name,
            character => Assert.True(
                char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_',
                $"'{character}' is not a character Windows allows in a container name"));
    }

    [Fact]
    public void TheSamePluginGetsTheSameContainerEveryTime()
    {
        // Otherwise every restart leaves another profile behind, and the grants Aurora applied
        // last time are on a container nothing runs in any more.
        Assert.Equal(
            AppContainerProfiles.NameFor("acme/notes"), AppContainerProfiles.NameFor("acme/notes"));
    }

    [Fact]
    public void TwoPluginsNeverShareAContainerEvenWhenTheirNamesFlattenTogether()
    {
        // Both transliterate to "acme-note-s". Sharing a container would mean sharing the
        // filesystem grants of whichever started first.
        Assert.NotEqual(
            AppContainerProfiles.NameFor("acme/note-s"), AppContainerProfiles.NameFor("acme/note_s"));
    }

    [Fact]
    public void AVeryLongPluginIdStillProducesADistinctName()
    {
        var first = AppContainerProfiles.NameFor(new string('a', 300) + "/one");
        var second = AppContainerProfiles.NameFor(new string('a', 300) + "/two");

        Assert.True(first.Length <= 64);
        Assert.True(second.Length <= 64);

        // Truncation is where two plugins quietly become one. The hash is at the end for exactly
        // this reason, and it must be what survives.
        Assert.NotEqual(first, second);
    }

    // ---- what the platform reports about itself ----

    [Fact]
    [SupportedOSPlatform("windows")]
    public void TheWindowsSandboxDescribesItselfAsConfining()
    {
        // The plan is pure — no interop — so it can be asked here. Whether the kernel then honours
        // what it describes is the part no test on this machine can reach.
        SandboxPlan plan = new WindowsAppContainerSandbox().Plan(Request());

        Assert.Equal(SandboxLevel.Confined, plan.Level);
        Assert.Equal("AppContainer", plan.Mechanism);
        Assert.Empty(plan.Unenforced);

        // No wrapper program: the confinement rides on the token, so the plan names the plugin
        // itself and the arguments stay the plugin's own.
        Assert.Equal(Request().Executable, plan.FileName);
        Assert.Empty(plan.Arguments);
    }
}
