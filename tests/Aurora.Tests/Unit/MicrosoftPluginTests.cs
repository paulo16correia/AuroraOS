using System.Diagnostics;
using Aurora.Core.Contracts;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// The Microsoft 365 plugin (docs/adr/0071, docs/integrations/microsoft.md).
/// </summary>
/// <remarks>
/// The plugin is Python and its tests are Python, run from here so the suite is one place to look.
/// A rule about not leaking a refresh token is not less important for being written in another
/// language, and a module that quietly stopped being collected would otherwise pass by running
/// nothing.
/// <para>
/// <b>Status.</b> IMPLEMENTED and TESTED against a loopback stand-in. <b>Not VERIFIED</b> — no
/// call has been made to Microsoft, because no tenant was available. See
/// <c>docs/reference/platform-support.md</c>.
/// </para>
/// </remarks>
public sealed class MicrosoftPluginTests
{
    private static DirectoryInfo Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "plugins")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!;
    }

    private static string PluginSource() =>
        Path.Combine(Repository().FullName, "plugins", "microsoft");

    private static string RunPython(string module, int expected)
    {
        using var python = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "python3",
                WorkingDirectory = PluginSource(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        python.StartInfo.ArgumentList.Add("-m");
        python.StartInfo.ArgumentList.Add("unittest");
        python.StartInfo.ArgumentList.Add(module);
        python.StartInfo.ArgumentList.Add("-v");

        python.Start();
        var output = python.StandardOutput.ReadToEnd() + python.StandardError.ReadToEnd();
        python.WaitForExit(180_000);

        Assert.True(python.ExitCode == 0, output);

        // Asserted so a module that stops being collected fails here rather than passing with
        // nothing run.
        Assert.Contains($"Ran {expected} test", output, StringComparison.Ordinal);

        return output;
    }

    [Fact]
    public void TheGraphTransportHoldsItsRules()
    {
        // The allowlist, the credential ordering, redaction, throttling, pagination and every
        // malformed-response path — against a stand-in that answers over a real socket.
        RunPython("test_graph", 37);
    }

    [Fact]
    public void ThePluginProtocolAndItsHandlingOfHostileContentHold()
    {
        RunPython("test_service", 12);
    }

    // ---- what the manifest promises ----

    [Fact]
    public void TheManifestDeclaresOnlyMicrosoftsOwnHosts()
    {
        PluginManifest manifest = Manifest();

        // The owner agrees to these at install, and the plugin's own allowlist agrees with them.
        // A third host here would be one nobody was asked about.
        Assert.Equal(
            ["graph.microsoft.com", "login.microsoftonline.com"],
            manifest.NetworkEndpoints.Order());
    }

    [Fact]
    public void EverySecretItAsksForSaysWhatItLetsAuroraDo()
    {
        PluginManifest manifest = Manifest();

        Assert.NotEmpty(manifest.RequiredSecrets ?? []);

        foreach (PluginSecretRequirement secret in manifest.RequiredSecrets!)
        {
            // The purpose is what a person reads before handing over a credential that acts as
            // them. "refresh_token" tells them nothing; the sentence has to.
            Assert.False(
                string.IsNullOrWhiteSpace(secret.Purpose),
                $"{secret.Name} asks for a credential without saying what it grants");
        }

        PluginSecretRequirement refresh =
            manifest.RequiredSecrets.Single(s => s.Name == "refresh_token");

        Assert.Contains("as you", refresh.Purpose, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadingTheDirectoryStillCostsADecision()
    {
        PluginCapability identity =
            Manifest().Capabilities.Single(c => c.Key == "microsoft.identity.me");

        // MEDIUM and approval-gated although it only reads. What it reads is the owner's
        // organisation — names, titles, departments — and that is worth one human decision even
        // though repeating it changes nothing. Being effect-free is what then lets a consent
        // session cover the next one.
        Assert.Equal(RiskLevel.Medium, identity.Risk);
        Assert.True(identity.ApprovalRequired);
        Assert.Empty(identity.Effects);
    }

    [Fact]
    public void AskingWhetherItIsConfiguredIsFree()
    {
        PluginCapability status = Manifest().Capabilities.Single(c => c.Key == "microsoft.status");

        // It contacts nobody, so it can be LOW and unapproved — which is the point. An owner has
        // to be able to find out what is missing before approving something that would find out
        // by failing.
        Assert.Equal(RiskLevel.Low, status.Risk);
        Assert.False(status.ApprovalRequired);
        Assert.Empty(status.Effects);
    }

    [Fact]
    public void NoCapabilityOpensAWindowOverAnother()
    {
        // docs/adr/0070 lets a capability name repeated authority. Nothing Microsoft-facing does,
        // and a window over sending mail is exactly the thing that should be argued for
        // separately rather than arriving with a family of read capabilities.
        Assert.All(Manifest().Capabilities, capability => Assert.Null(capability.OpensWindow));
    }

    /// <summary>
    /// The environment the test harness relies on being clean.
    /// </summary>
    /// <remarks>
    /// The plugin reads <c>AURORA_MICROSOFT_BASE</c> to point itself at a stand-in, which is only
    /// safe because a plugin's environment is built by Aurora rather than inherited. This asserts
    /// that property over the source of both hosts, so the seam cannot quietly become reachable
    /// in production by somebody removing one line.
    /// </remarks>
    [Fact]
    public void APluginsEnvironmentIsBuiltRatherThanInherited()
    {
        foreach (var host in new[] { "SubprocessPluginHost.cs", "ServiceProcess.cs" })
        {
            var source = File.ReadAllText(
                Path.Combine(Repository().FullName, "src", "Aurora.Adapters", "Plugins", host));

            Assert.True(
                source.Contains("Environment.Clear()", StringComparison.Ordinal)
                || source.Contains("new Dictionary<string, string>", StringComparison.Ordinal),
                $"{host} no longer builds the child's environment from nothing");
        }
    }

    private static PluginManifest Manifest()
    {
        var json = File.ReadAllText(Path.Combine(PluginSource(), "plugin.json"));
        PluginManifestRead read = PluginManifestReader.Read(json, []);

        Assert.Empty(read.Problems);
        return read.Manifest!;
    }
}
