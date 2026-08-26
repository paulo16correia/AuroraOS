using Aurora.Core.Contracts;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Reading a community author's <c>plugin.json</c> (docs/adr/0062).
/// </summary>
/// <remarks>
/// The error messages are the feature under test. A plugin author's first encounter with Aurora is
/// this file being wrong, and "invalid manifest" teaches them nothing — so what is asserted here is
/// mostly that the right thing is said, and that everything wrong is said at once.
/// </remarks>
public sealed class PluginManifestTests
{
    private static readonly string[] BuiltIn = ["echo.say", "files.write_sandbox"];

    private const string Good = """
        {
          "plugin_id": "acme/notes",
          "version": "1.0.0",
          "publisher": "acme",
          "executable": "run.py",
          "max_data_class": "PRIVATE",
          "required_permissions": ["notes.write"],
          "capabilities": [
            {
              "key": "notes.append",
              "title": "Append a note",
              "description": "Adds a line to a notebook.",
              "input_schema": { "type": "object" },
              "effects": ["notes.write"],
              "risk": "MEDIUM",
              "approval_required": true
            }
          ]
        }
        """;

    private static PluginManifestRead Read(string json) =>
        PluginManifestReader.Read(json, BuiltIn);

    [Fact]
    public void AWellFormedManifestBecomesAManifest()
    {
        PluginManifestRead read = Read(Good);

        Assert.True(read.Ok, string.Join("; ", read.Problems));
        Assert.Equal("acme/notes", read.Manifest!.PluginId);
        Assert.Equal("run.py", read.Manifest.Executable);

        PluginCapability capability = Assert.Single(read.Manifest.Capabilities);
        Assert.Equal("notes.append", capability.Key);
        Assert.Equal(RiskLevel.Medium, capability.Risk);
        Assert.Equal("Append a note", capability.Title);

        // Neither is the author's to write: the owner is the trust anchor, so Aurora seals what
        // was approved rather than checking a signature it has no way to attribute.
        Assert.Empty(read.Manifest.Signature);
        Assert.Empty(read.Manifest.IntegrityHash);
    }

    [Fact]
    public void EverythingWrongIsReportedAtOnce()
    {
        PluginManifestRead read = Read("""
            {
              "plugin_id": "",
              "version": "1.0.0",
              "publisher": "acme",
              "executable": "/usr/bin/python3",
              "max_data_class": "TOP_SECRET",
              "typo_field": 1,
              "capabilities": [
                { "key": "greet", "title": "", "risk": "SPICY", "input_schema": "not an object" }
              ]
            }
            """);

        Assert.False(read.Ok);

        // Somebody fixing six mistakes should need one round trip, not six. A reader that stopped
        // at the first would turn every one of these into a separate visit.
        Assert.Contains(read.Problems, p => p.Contains("'typo_field' is not a field", StringComparison.Ordinal));
        Assert.Contains(read.Problems, p => p.Contains("plugin_id is missing", StringComparison.Ordinal));
        Assert.Contains(read.Problems, p => p.Contains("must be relative", StringComparison.Ordinal));
        Assert.Contains(read.Problems, p => p.Contains("TOP_SECRET", StringComparison.Ordinal));
        Assert.Contains(read.Problems, p => p.Contains("title is missing", StringComparison.Ordinal));
        Assert.Contains(read.Problems, p => p.Contains("must be dotted", StringComparison.Ordinal));
        Assert.Contains(read.Problems, p => p.Contains("'SPICY'", StringComparison.Ordinal));
        Assert.Contains(read.Problems, p => p.Contains("input_schema must be", StringComparison.Ordinal));
    }

    [Fact]
    public void APluginCannotClaimAnActionAuroraAlreadyHas()
    {
        PluginManifestRead read = Read(Good.Replace("notes.append", "echo.say", StringComparison.Ordinal));

        // A plugin shadowing files.write_sandbox would be the most valuable bug in the system to
        // whoever wrote the plugin.
        Assert.False(read.Ok);
        Assert.Contains(read.Problems, p => p.Contains("already has an action", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("aurora.anything")]
    [InlineData("kernel.commit")]
    [InlineData("mind.write")]
    public void AuroraSOwnNamespacesAreReserved(string key)
    {
        PluginManifestRead read = Read(Good.Replace("notes.append", key, StringComparison.Ordinal));

        Assert.False(read.Ok);
        Assert.Contains(read.Problems, p => p.Contains("reserved for Aurora", StringComparison.Ordinal));
    }

    [Fact]
    public void SomethingAboveLowThatDidNotAskForApprovalIsRefusedRatherThanInstalledAndUseless()
    {
        PluginManifestRead read = Read(Good.Replace(
            "\"approval_required\": true", "\"approval_required\": false", StringComparison.Ordinal));

        // Policy denies anything above LOW that did not opt in, so this manifest would install
        // and then never be allowed to run. Better to say so while somebody is still writing it.
        Assert.False(read.Ok);
        Assert.Contains(read.Problems, p => p.Contains("policy will refuse", StringComparison.Ordinal));
    }

    [Fact]
    public void HighWithoutAWayBackIsRefused()
    {
        PluginManifestRead read = Read(Good.Replace(
            "\"risk\": \"MEDIUM\"", "\"risk\": \"HIGH\"", StringComparison.Ordinal));

        // The same rule Aurora's own capabilities are held to (docs/adr/0060): at HIGH, one yes
        // is not enough on its own.
        Assert.False(read.Ok);
        Assert.Contains(read.Problems, p => p.Contains("also reversible", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSameKeyTwiceInOneManifestIsRefused()
    {
        PluginManifestRead read = Read("""
            {
              "plugin_id": "acme/notes", "version": "1.0.0", "publisher": "acme",
              "executable": "run.py",
              "capabilities": [
                { "key": "notes.append", "title": "One", "risk": "LOW",
                  "approval_required": false, "input_schema": { "type": "object" } },
                { "key": "notes.append", "title": "Two", "risk": "LOW",
                  "approval_required": false, "input_schema": { "type": "object" } }
              ]
            }
            """);

        Assert.False(read.Ok);
        Assert.Contains(read.Problems, p => p.Contains("declared twice", StringComparison.Ordinal));
    }

    [Fact]
    public void MalformedJsonSaysWhereRatherThanThatItIsInvalid()
    {
        PluginManifestRead read = Read("{ \"plugin_id\": ");

        Assert.False(read.Ok);
        var problem = Assert.Single(read.Problems);

        // The parser names the line and column, which is more use than anything this could say
        // instead.
        Assert.Contains("not valid JSON", problem, StringComparison.Ordinal);
        Assert.Contains("LineNumber", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestWithNoCapabilitiesCannotBeInstalled()
    {
        PluginManifestRead read = Read("""
            { "plugin_id": "acme/nothing", "version": "1.0.0", "publisher": "acme",
              "executable": "run.py", "capabilities": [] }
            """);

        Assert.False(read.Ok);
        Assert.Contains(read.Problems, p => p.Contains("offers nothing", StringComparison.Ordinal));
    }

    [Fact]
    public void AManifestAskingForTheNetworkIsRefused()
    {
        PluginManifestRead read = Read(Good.Replace(
            "\"required_permissions\": [\"notes.write\"],",
            "\"required_permissions\": [\"notes.write\"], \"network_endpoints\": [\"api.acme.com\"],",
            StringComparison.Ordinal));

        // RFC 060 rule 1 asks a plugin to declare its network domains and rule 2 says it runs
        // without the general network. Aurora resolves the two strictly: the sandbox denies the
        // network, so a declared endpoint is a request it cannot grant rather than a limit it
        // could enforce. Better to say so while somebody is still writing the file.
        Assert.False(read.Ok);
        Assert.Contains(
            read.Problems, p => p.Contains("denies plugins the network", StringComparison.Ordinal));
    }
}
