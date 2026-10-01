using Aurora.Server;
using Aurora.Tests.Support;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// The suite does not write to the owner's installation (docs/adr/0083).
/// </summary>
/// <remarks>
/// The hazard is not hypothetical. A test in the Discord plugin wrote a stub over the installed
/// <c>piper.exe</c> and deleted it in teardown, taking a working speech engine off the machine it
/// was testing; the same shape on the .NET side would reach the live database, the live keys, and
/// the passphrase file that holds the operator's failed-attempt count and lockout.
/// <para>
/// These tests pin the mechanism rather than the absence of damage. "Nothing was harmed this run"
/// is a measurement that stops being true the moment somebody adds a test; "an unconfigured path
/// throws" holds for every test that will ever be written.
/// </para>
/// </remarks>
public sealed class DeploymentIsolationTests
{
    private static string DeploymentRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aurora");

    private static IConfiguration Configured(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings
                .Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))
                .Append(new KeyValuePair<string, string?>(
                    "Aurora:BearerToken", "a-token-long-enough-to-pass-validation"))
                .ToList())
            .Build();

    // ---- the guard is on, and it is on for everything ----

    [Fact]
    public void TheSuiteRunsWithImplicitDeploymentPathsForbidden()
    {
        // Armed by a module initializer, so it covers tests written after this one too.
        Assert.Equal(
            "1",
            Environment.GetEnvironmentVariable(AuroraServerOptions.RequireExplicitPathsVariable));
    }

    [Fact]
    public void AnUnconfiguredDatabasePathThrowsRatherThanBorrowingTheDeployment()
    {
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
            () => AuroraServerOptions.FromConfiguration(Configured()));

        // The message has to say what to do. A test that fails with "path not configured" and no
        // mention of the deployment invites somebody to hardcode the deployment path to fix it.
        Assert.Contains("Aurora:DbPath", refused.Message, StringComparison.Ordinal);
        Assert.Contains("deployment", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnconfiguredSandboxRootThrowsToo()
    {
        // The sandbox is the one that bit: resolving it does not merely compute a path, it creates
        // the directory and re-applies owner-only hardening to it — on the live sandbox, from a
        // unit test about configuration defaults.
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
            () => AuroraServerOptions.FromConfiguration(
                Configured(("Aurora:DbPath", TestTemp.Path("iso") + ".db"))));

        Assert.Contains("Aurora:SandboxRoot", refused.Message, StringComparison.Ordinal);
    }

    // ---- what the suite actually builds points somewhere else ----

    [Fact]
    public void ConfiguredOptionsNeverResolveIntoTheDeployment()
    {
        AuroraServerOptions options = AuroraServerOptions.FromConfiguration(Configured(
            ("Aurora:DbPath", TestTemp.Path("iso-db") + ".db"),
            ("Aurora:SandboxRoot", TestTemp.Folder("iso-sandbox"))));

        // Every path the server will write to, checked against the one directory it must not.
        // Named individually rather than as a loop, so a path added later is a compile error here
        // rather than a gap nobody notices.
        foreach (var path in new[]
        {
            options.DbPath, options.SandboxRoot, options.AuditKeyPath, options.AuditAnchorPath,
            options.VaultKeyPath, options.PluginKeyPath, options.SnapshotKeyPath,
            options.GenomeKeyPath, options.DeliberationKeyPath, options.PassphrasePath,
            options.PluginRoot,
        })
        {
            Assert.False(
                IsInside(path, DeploymentRoot()),
                $"'{path}' resolves inside the owner's installation at '{DeploymentRoot()}'");
        }
    }

    [Fact]
    public void TheApplicationFactoryKeepsItsWholeDeploymentOutOfTheRealOne()
    {
        // The factory starts the real Program, which builds the real service graph — the one path
        // in the suite that creates keys rather than only naming them.
        var factory = new AuroraAppFactory();

        foreach (var path in new[] { factory.DbPath, factory.SandboxRoot, factory.PassphrasePath })
        {
            Assert.False(
                IsInside(path, DeploymentRoot()),
                $"the factory would write '{path}' inside the owner's installation");
        }
    }

    // ---- the operator's own state, named ----

    [Theory]
    [InlineData("aurora.db")]
    [InlineData("aurora.deliberation.key")]
    [InlineData("aurora.audit.key")]
    [InlineData("aurora.vault.key")]
    [InlineData("aurora.plugin.key")]
    [InlineData("aurora.passphrase.json")]
    public void NoTestPathEverNamesTheOperatorsOwnFile(string name)
    {
        // The passphrase file is the sharpest of these: it holds the failed-attempt count and the
        // lockout, so a test that rewrote it could lock a person out of approving anything, or
        // quietly clear the evidence that somebody had been trying.
        AuroraServerOptions options = AuroraServerOptions.FromConfiguration(Configured(
            ("Aurora:DbPath", TestTemp.Path("iso-named") + ".db"),
            ("Aurora:SandboxRoot", TestTemp.Folder("iso-named-sandbox"))));

        var theirs = Path.Combine(DeploymentRoot(), name);

        Assert.DoesNotContain(theirs, new[]
        {
            options.DbPath, options.AuditKeyPath, options.VaultKeyPath, options.PluginKeyPath,
            options.SnapshotKeyPath, options.GenomeKeyPath, options.DeliberationKeyPath,
            options.PassphrasePath,
        }, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether a path lies within a directory, compared as paths rather than as text.</summary>
    private static bool IsInside(string path, string directory)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(directory).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
