using Aurora.Adapters.Diagnostics;
using Aurora.Adapters.Files;
using Aurora.Adapters.Plugins;
using Aurora.Adapters.Plugins.Sandboxes;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// The doctor's preflight checks (F-2, docs/adr/0079), exercised without a server or a console.
/// </summary>
public sealed class PreflightTests
{
    private static PreflightCheck One(IReadOnlyList<PreflightCheck> checks, string componentContains) =>
        checks.Single(c => c.Component.Contains(componentContains, StringComparison.Ordinal));

    // ---- key material ----

    [Fact]
    public void AnOwnerOnlyKeyPasses()
    {
        var path = Path.Combine(TestTemp.Folder("pf"), "aurora.audit.key");
        File.WriteAllBytes(path, new byte[32]);
        OwnerOnly.File(path);

        PreflightCheck check = One(
            Preflight.KeyMaterial([new Preflight.KeyFile("audit", path)]), "audit");

        Assert.Equal(PreflightStatus.Pass, check.Status);
    }

    [Fact]
    public void AKeyThatIsNotOwnerOnlyFails()
    {
        var path = Path.Combine(TestTemp.Folder("pf"), "exposed.key");
        File.WriteAllBytes(path, new byte[32]);
        OwnerOnly.File(path);
        Broaden(path);

        PreflightCheck check = One(
            Preflight.KeyMaterial([new Preflight.KeyFile("audit", path)]), "audit");

        Assert.Equal(PreflightStatus.Fail, check.Status);
    }

    [Fact]
    public void AnAbsentButCreatableKeyIsInfoNotFail()
    {
        var path = Path.Combine(TestTemp.Folder("pf"), "will-exist.key");

        PreflightCheck check = One(
            Preflight.KeyMaterial([new Preflight.KeyFile("audit", path)]), "audit");

        Assert.Equal(PreflightStatus.Info, check.Status);
    }

    // ---- filesystem ----

    [Fact]
    public void AWritableDatabaseDirectoryPasses()
    {
        var dir = TestTemp.Folder("pf-db");
        var db = Path.Combine(dir, "aurora.db");

        PreflightCheck check = One(Preflight.Filesystem(db, Path.Combine(dir, "sandbox")), "database");

        Assert.Equal(PreflightStatus.Pass, check.Status);
    }

    // ---- configuration ----

    [Fact]
    public void UnconfinedPluginsIsAWarning()
    {
        Assert.Equal(
            PreflightStatus.Warn,
            One(Preflight.Configuration(allowUnconfinedPlugins: true), "configuration").Status);
    }

    [Fact]
    public void ConfinedByDefaultPasses()
    {
        Assert.Equal(
            PreflightStatus.Pass,
            One(Preflight.Configuration(allowUnconfinedPlugins: false), "configuration").Status);
    }

    // ---- sandbox ----

    [Fact]
    public void AConfinedSandboxPassesAndAnUnconfinedOneWarns()
    {
        Assert.Equal(
            PreflightStatus.Pass,
            One(Preflight.Sandbox(new AlwaysConfinedSandbox(), TestTemp.Folder("pf-s")), "confinement").Status);

        Assert.Equal(
            PreflightStatus.Warn,
            One(Preflight.Sandbox(new UnconfinedSandbox("no sandbox here"), TestTemp.Folder("pf-s2")),
                "confinement").Status);
    }

    // ---- plugins ----

    [Fact]
    public void AValidPluginWithAResolvableInterpreterAndPresentSecretPasses()
    {
        var installed = TestTemp.Folder("pf-plugin");
        var script = Path.Combine(installed, "run.py");
        File.WriteAllText(script, "#!/usr/bin/env python3\nprint()\n");

        PluginInstallation installation = Installation(script, "you/notes", secret: "token");

        IReadOnlyList<PreflightCheck> checks = Preflight.Plugins(
            [installation],
            // Not-Windows resolver: the platform runs the program directly, so interpreter
            // resolution is a pass everywhere the test runs.
            new PluginInterpreters(windows: false),
            secretPresent: (_, _) => true);

        Assert.Equal(PreflightStatus.Pass, One(checks, "manifest").Status);
        Assert.Equal(PreflightStatus.Pass, One(checks, "secret").Status);
        Assert.DoesNotContain(checks, c => c.Status == PreflightStatus.Fail);
    }

    [Fact]
    public void AMissingSecretFails()
    {
        var installed = TestTemp.Folder("pf-plugin2");
        var script = Path.Combine(installed, "run.py");
        File.WriteAllText(script, "print()\n");

        PluginInstallation installation = Installation(script, "you/notes", secret: "token");

        IReadOnlyList<PreflightCheck> checks = Preflight.Plugins(
            [installation], new PluginInterpreters(windows: false), secretPresent: (_, _) => false);

        PreflightCheck secret = One(checks, "secret");
        Assert.Equal(PreflightStatus.Fail, secret.Status);

        // The diagnostic tells the operator how to fix it and never carries a value.
        Assert.Contains("secret set", secret.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInvalidStoredManifestFails()
    {
        var installation = new PluginInstallation(
            "id", "you/broken", "1.0.0", "you", InstallationStatus.Installed,
            [], "{ not valid json", "now", "now", 0);

        IReadOnlyList<PreflightCheck> checks = Preflight.Plugins(
            [installation], new PluginInterpreters(windows: false), secretPresent: (_, _) => true);

        Assert.Equal(PreflightStatus.Fail, One(checks, "manifest").Status);
    }

    [Fact]
    public void NoInstalledPluginsIsInfo()
    {
        IReadOnlyList<PreflightCheck> checks = Preflight.Plugins(
            [], new PluginInterpreters(windows: false), secretPresent: (_, _) => true);

        Assert.Equal(PreflightStatus.Info, One(checks, "plugins").Status);
    }

    private static PluginInstallation Installation(string executable, string pluginId, string secret)
    {
        // Stored the way the registry stores it: a sealed PluginManifest with an absolute
        // executable, serialized with AuroraJson (not a plugin.json).
        var manifest = new PluginManifest(
            pluginId, "1.0.0", "you", Signature: "sig", MinPlatformVersion: 1,
            Capabilities:
            [
                new PluginCapability(
                    "notes.write", "{}", "{}", [], ApprovalRequired: false, RateLimitPerMinute: 60,
                    Timeout: TimeSpan.FromSeconds(10), IdempotencySupport: false, AuditLevel: "FULL"),
            ],
            EventSubscriptions: [], RequiredPermissions: [],
            MaxDataClass: Sensitivity.Private, NetworkEndpoints: [],
            DocumentationRef: "docs", IntegrityHash: "hash",
            Executable: executable,
            Service: new PluginService(executable, TimeSpan.FromSeconds(10)),
            RequiredSecrets: [new PluginSecretRequirement(secret, "why it is needed")],
            Interpreter: "python3");

        return new PluginInstallation(
            "install-id", pluginId, "1.0.0", "you", InstallationStatus.Installed,
            [], AuroraJson.Serialize(manifest), "now", "now", 0);
    }

    private static void Broaden(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new FileInfo(path);
            System.Security.AccessControl.FileSecurity acl = info.GetAccessControl();
            acl.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
                System.Security.AccessControl.FileSystemRights.Read,
                System.Security.AccessControl.AccessControlType.Allow));
            info.SetAccessControl(acl);
        }
        else
        {
            File.SetUnixFileMode(
                path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        }
    }

    /// <summary>A sandbox that reports full confinement, for the PASS branch off-Windows.</summary>
    private sealed class AlwaysConfinedSandbox : IPluginSandbox
    {
        public SandboxPlan Plan(SandboxRequest request) =>
            new(request.Executable, [], SandboxLevel.Confined, "test-confinement", []);

        public Task<SandboxStart> StartAsync(SandboxLaunch launch, CancellationToken ct) =>
            throw new NotSupportedException("plan only");
    }
}
