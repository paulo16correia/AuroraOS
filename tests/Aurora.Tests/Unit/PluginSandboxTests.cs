using System.Globalization;
using Aurora.Adapters.Events;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Plugins;
using Aurora.Adapters.Plugins.Sandboxes;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// The second half of RFC 060 rule 2: a plugin runs without access to the general network, and
/// without the reach of the owner who started Aurora.
/// </summary>
/// <remarks>
/// These tests run a real program under the real platform sandbox and check that the operating
/// system stops it. A test that asserted on the generated profile text would pass just as happily
/// against a profile that permits everything.
/// </remarks>
public sealed class PluginSandboxTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly byte[] Key = Enumerable.Repeat((byte)9, 32).ToArray();

    /// <summary>
    /// True where a plugin can be written in three lines of shell and the OS can confine it.
    /// </summary>
    /// <remarks>
    /// macOS only, deliberately. Linux confinement needs bubblewrap, which is not installed
    /// everywhere; asserting that the kernel blocked a connection when the sandbox silently was
    /// not applied is exactly the false pass this file exists to avoid. See ADR 0052 for what has
    /// and has not been run.
    /// </remarks>
    private static bool Confinable => OperatingSystem.IsMacOS() && File.Exists("/usr/bin/sandbox-exec");

    private static PluginManifest Manifest(string executable) => new(
        "plugin/probe", "1.0.0", "acme", "", MinPlatformVersion: 1,
        Capabilities:
        [
            new PluginCapability(
                "probe.run", "{}", "{}", ["probe"], ApprovalRequired: false,
                RateLimitPerMinute: 30, Timeout: TimeSpan.FromSeconds(20),
                IdempotencySupport: false, AuditLevel: "FULL"),
        ],
        EventSubscriptions: [],
        RequiredPermissions: ["probe"],
        MaxDataClass: Sensitivity.Private,
        NetworkEndpoints: [],
        DocumentationRef: "docs/plugin/probe",
        IntegrityHash: "")
    {
        Executable = executable,
    };

    private static PluginInvocation Call() => new("plugin/probe", "probe.run", "{}", Sensitivity.Private);

    /// <summary>Writes a shell plugin and returns (root, path).</summary>
    private static async Task<(string Root, string Script)> PluginAsync(string body)
    {
        var root = TestTemp.Path("sbx");
        Directory.CreateDirectory(root);
        var script = Path.Combine(root, "probe.sh");

        // Every plugin drains stdin first: Aurora writes the invocation and closes, and a plugin
        // that never reads leaves the host writing into a pipe nobody is emptying.
        await File.WriteAllTextAsync(script, $"#!/bin/sh\ncat > /dev/null\n{body}\n", Ct);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return (root, script);
    }

    private static void TryDelete(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives the test is not a failed test.
        }
    }

    [Fact]
    public async Task APluginCannotOpenANetworkConnection()
    {
        if (!Confinable)
        {
            return;
        }

        // Dials a well-known resolver on a raw IP, so nothing here depends on DNS working — or on
        // this machine having a working internet connection, which would make the test lie when
        // run offline. Under confinement the socket call fails before any of that matters.
        (string root, string script) = await PluginAsync(
            "if /usr/bin/nc -z -w 2 1.1.1.1 53 2>/dev/null; then printf '{\"net\":\"open\"}'; "
            + "else printf '{\"net\":\"denied\"}'; fi");

        try
        {
            var host = new SubprocessPluginHost(root);
            PluginResult result = await host.InvokeAsync(Manifest(script), Call(), Ct);

            Assert.True(result.Ok, result.Detail);
            Assert.Equal("""{"net":"denied"}""", result.OutputJson);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task APluginCannotReadTheOwnerSFiles()
    {
        if (!Confinable)
        {
            return;
        }

        // Aurora's database and its four key files live under the owner's home. A plugin that can
        // read there has the vault whatever the manifest said it was allowed.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        (string root, string script) = await PluginAsync(
            $"if /bin/ls '{home}' >/dev/null 2>&1; then printf '{{\"home\":\"read\"}}'; "
            + "else printf '{\"home\":\"denied\"}'; fi");

        try
        {
            var host = new SubprocessPluginHost(root);
            PluginResult result = await host.InvokeAsync(Manifest(script), Call(), Ct);

            Assert.True(result.Ok, result.Detail);
            Assert.Equal("""{"home":"denied"}""", result.OutputJson);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task APluginWritesInItsOwnDirectoryAndNowhereElse()
    {
        if (!Confinable)
        {
            return;
        }

        var elsewhere = TestTemp.Path("elsewhere") + ".txt";

        (string root, string script) = await PluginAsync(
            $"echo out > '{elsewhere}' 2>/dev/null && printf '{{\"out\":\"wrote\"}}' || "
            + "(echo in > ./inside.txt && printf '{\"out\":\"denied\"}')");

        try
        {
            var host = new SubprocessPluginHost(root);
            PluginResult result = await host.InvokeAsync(Manifest(script), Call(), Ct);

            Assert.True(result.Ok, result.Detail);
            Assert.Equal("""{"out":"denied"}""", result.OutputJson);
            Assert.False(File.Exists(elsewhere));

            // And the one place it may write is its own working directory, not the plugin root
            // and not the directory it was installed in.
            Assert.True(File.Exists(Path.Combine(root, "plugin/probe", "inside.txt")));
        }
        finally
        {
            File.Delete(elsewhere);
            TryDelete(root);
        }
    }

    [Fact]
    public async Task APlatformThatCannotConfineGetsARefusalRatherThanAnUnconfinedRun()
    {
        (string root, string script) = await PluginAsync("printf '{\"ran\":true}'");

        try
        {
            var host = new SubprocessPluginHost(
                root, new UnconfinedSandbox("this platform has no sandbox"));

            PluginResult result = await host.InvokeAsync(Manifest(script), Call(), Ct);

            // The plugin did not run. Before ADR 0052 it ran, and looked exactly like a confined
            // one, which is why the gap survived so long.
            Assert.False(result.Ok);
            Assert.Equal(PluginRefusal.SandboxUnavailable, result.Refusal);

            // The refusal has to carry enough for the owner to decide, not just that it refused.
            Assert.Contains("this platform has no sandbox", result.Detail, StringComparison.Ordinal);
            Assert.Contains("network connections", result.Detail, StringComparison.Ordinal);
            Assert.Contains("Aurora:Plugins:AllowUnconfined", result.Detail, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task TheOwnerCanAcceptAnUnconfinedRun()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        (string root, string script) = await PluginAsync("printf '{\"ran\":true}'");

        try
        {
            var host = new SubprocessPluginHost(
                root, new UnconfinedSandbox("no sandbox"), allowUnconfined: true);

            PluginResult result = await host.InvokeAsync(Manifest(script), Call(), Ct);

            // Someone running a plugin they wrote themselves is not to be blocked by a security
            // property that exists to protect them from strangers.
            Assert.True(result.Ok, result.Detail);
            Assert.Equal("""{"ran":true}""", result.OutputJson);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task APlatformWithoutASandboxDoesNotMakeThePluginLookUntrustworthy()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var db = new SqliteTestDb();
        var clock = new TestClock(
            DateTimeOffset.Parse("2026-01-15T09:00:00+00:00", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind));

        (string root, string script) = await PluginAsync("printf '{\"ran\":true}'");

        try
        {
            var host = new SubprocessPluginHost(root, new UnconfinedSandbox("no sandbox here"));
            var bus = new SqliteEventBus(
                db.Factory, new SqliteOutbox(new PermissiveEventCatalogue(), clock), clock);

            var registry = new SqlitePluginRegistry(
                db.Factory, host, bus, Enumerable.Repeat((byte)9, 32).ToArray(), clock);

            PluginManifest draft = Manifest(script) with
            {
                Signature = SqlitePluginRegistry.Sign(Key, "plugin/probe", "1.0.0", "acme"),
            };

            PluginManifest manifest = draft with { IntegrityHash = SqlitePluginRegistry.HashOf(draft) };
            await registry.InstallAsync(manifest, ["probe"], "owner", Ct);

            // Three refusals: one more than the circuit tolerates, if they counted.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                PluginResult refused = await registry.InvokeAsync(Call(), Ct);
                Assert.Equal(PluginRefusal.SandboxUnavailable, refused.Refusal);
            }

            PluginInstallation after = (await registry.GetAsync("plugin/probe", Ct))!;

            // Still installed, still at zero failures. The machine could not confine it; that is
            // not a fact about the plugin, and quarantining it would leave the owner reinstating
            // something that never misbehaved.
            Assert.Equal(InstallationStatus.Installed, after.Status);
            Assert.Equal(0, after.ConsecutiveFailures);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void TheAbsenceOfASandboxIsNeverReportedAsConfinement()
    {
        SandboxPlan plan = new UnconfinedSandbox("nothing here")
            .Plan(new SandboxRequest("plugin/probe", "/bin/echo", "/tmp/wd"));

        Assert.Equal(SandboxLevel.Process, plan.Level);

        // And it says what is missing, in three separate statements rather than one word. The
        // point of the seam is that the caller can refuse against a description.
        Assert.Equal(3, plan.Unenforced.Count);
    }

    [Fact]
    public void ADirectoryNameCannotWriteItsOwnSandboxPolicy()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        // A quote in a path would close the SBPL string and turn everything after it into rules —
        // the sandbox equivalent of a SQL injection, and reachable by anyone who can name a
        // directory.
        SandboxPlan plan = new MacOsSandbox().Plan(new SandboxRequest(
            "plugin/probe", "/tmp/ev\"il/(allow network*)/run.sh", "/tmp/wd"));

        var profile = plan.Arguments[1];

        Assert.Contains("\\\"", profile, StringComparison.Ordinal);
        Assert.EndsWith("(deny network*)", profile, StringComparison.Ordinal);
    }

    // ---- what each platform actually gets, asserted rather than assumed ----

    [Fact]
    public void ThisMachineGetsTheStrongestConfinementItCanDeliver()
    {
        SandboxPlan plan = PluginSandbox.ForThisMachine()
            .Plan(new SandboxRequest("plugin/probe", "/bin/echo", Path.GetTempPath()));

        if (OperatingSystem.IsMacOS())
        {
            // VERIFIED here: the tests above run real programs under this and the kernel stops
            // them.
            Assert.Equal(SandboxLevel.Confined, plan.Level);
            Assert.Equal("sandbox-exec", plan.Mechanism);
        }
        else if (OperatingSystem.IsLinux())
        {
            // UNVERIFIED unless bubblewrap is present. The flags are its documented interface and
            // the policy mirrors the macOS one, but nothing here has run them (docs/adr/0052).
            Assert.Equal(
                File.Exists("/usr/bin/bwrap") || File.Exists("/bin/bwrap")
                    ? SandboxLevel.Confined
                    : SandboxLevel.Process,
                plan.Level);
        }
        else if (OperatingSystem.IsWindows())
        {
            // UNSUPPORTED, and safe by default: no confinement is available, so the host refuses
            // to invoke rather than running third-party code loose.
            Assert.Equal(SandboxLevel.Process, plan.Level);
            Assert.Contains("AppContainer", plan.Mechanism, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WhereConfinementIsUnavailableTheDefaultIsToNotRun()
    {
        (string root, string script) = await PluginAsync("printf '{\"ran\":true}'");

        try
        {
            // The default constructor argument, and the default configuration value, both say
            // false. A platform Aurora cannot confine gets a refusal, and turning that into an
            // opt-in was a decision somebody had to write down (docs/adr/0052).
            var host = new SubprocessPluginHost(root, new UnconfinedSandbox("no sandbox here"));

            PluginResult result = await host.InvokeAsync(Manifest(script), Call(), Ct);

            Assert.False(result.Ok);
            Assert.Equal(PluginRefusal.SandboxUnavailable, result.Refusal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void TheLinuxPlanIsWhatBubblewrapDocumentsEvenWhereItCannotRun()
    {
        // Deterministic without bubblewrap installed: the plan is a value, and asserting its shape
        // is the most that can be checked from a Mac. Whether the kernel honours it is UNVERIFIED
        // and is marked so in docs/adr/0052 and the platform table.
        SandboxPlan plan = new LinuxSandbox("/usr/bin/bwrap")
            .Plan(new SandboxRequest("plugin/probe", "/opt/plug/run.py", "/tmp/wd"));

        Assert.Equal("/usr/bin/bwrap", plan.FileName);
        Assert.Equal(SandboxLevel.Confined, plan.Level);

        // The three that matter: no network namespace, no privilege, and it dies with Aurora.
        Assert.Contains("--unshare-net", plan.Arguments);
        Assert.Contains("--cap-drop", plan.Arguments);
        Assert.Contains("--die-with-parent", plan.Arguments);

        // The working directory is the one writable bind, and the plugin's own folder is read-only.
        Assert.Contains("--ro-bind", plan.Arguments);
        Assert.Contains("--bind", plan.Arguments);
        Assert.EndsWith("--", plan.Arguments[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void BubblewrapIsNotLookedForOnThePath()
    {
        // PATH is inherited from whoever started Aurora and can name a directory anybody can write
        // to. A sandbox found that way could be a program that pretends to sandbox.
        var source = File.ReadAllText(
            Path.Combine(
                new DirectoryInfo(AppContext.BaseDirectory).Parent!.Parent!.Parent!.Parent!.Parent!.FullName,
                "src", "Aurora.Adapters", "Plugins", "Sandboxes", "LinuxSandbox.cs"));

        Assert.Contains("/usr/bin/bwrap", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable(\"PATH\")", source, StringComparison.Ordinal);
    }
}
