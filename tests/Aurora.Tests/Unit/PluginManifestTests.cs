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

    /// <summary>A manifest whose opener asks to cover repeated calls to its own writer.</summary>
    private const string WithWindow = """
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
            },
            {
              "key": "notes.session",
              "title": "Keep a notebook open",
              "description": "Appends for a while without asking each time.",
              "input_schema": { "type": "object" },
              "effects": ["notes.write"],
              "risk": "MEDIUM",
              "approval_required": true,
              "opens_window_for": {
                "actions": ["notes.append"],
                "max_actions": 20,
                "lifetime_seconds": 600
              }
            }
          ]
        }
        """;

    /// <summary>
    /// The same two capabilities, with the three claims a window is checked against varied.
    /// </summary>
    private static string Window(string risk, bool opener, bool covered) =>
        $$"""
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
              "risk": "{{risk}}",
              "reversible": true,
              "approval_required": {{(covered ? "true" : "false")}}
            },
            {
              "key": "notes.session",
              "title": "Keep a notebook open",
              "description": "Appends for a while without asking each time.",
              "input_schema": { "type": "object" },
              "effects": ["notes.write"],
              "risk": "{{(opener ? "MEDIUM" : "LOW")}}",
              "approval_required": {{(opener ? "true" : "false")}},
              "opens_window_for": {
                "actions": ["notes.append"],
                "max_actions": 20,
                "lifetime_seconds": 600
              }
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
    public void AManifestNamesEachHostItReaches()
    {
        PluginManifestRead named = Read(Good.Replace(
            "\"required_permissions\": [\"notes.write\"],",
            "\"required_permissions\": [\"notes.write\"], \"network_endpoints\": [\"api.acme.com\"],",
            StringComparison.Ordinal));

        // Grantable since docs/adr/0067: a named host is something the owner can weigh.
        Assert.True(named.Ok, string.Join("; ", named.Problems));

        foreach (var vague in new[] { "*.acme.com", "https://acme.com", "acme.com/v1", "acme.com:443" })
        {
            PluginManifestRead read = Read(Good.Replace(
                "\"required_permissions\": [\"notes.write\"],",
                $"\"required_permissions\": [\"notes.write\"], \"network_endpoints\": [\"{vague}\"],",
                StringComparison.Ordinal));

            // A wildcard is not a name, and neither is a URL. Said while somebody is still writing
            // the file rather than at install, when they are already committed.
            Assert.False(read.Ok);
            Assert.Contains(
                read.Problems, p => p.Contains("plain host name", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ASecretIsDeclaredByNameAndNeverByValue()
    {
        PluginManifestRead read = Read(Good.Replace(
            "\"required_permissions\": [\"notes.write\"],",
            "\"required_permissions\": [\"notes.write\"], "
            + "\"required_secrets\": [{\"name\": \"bot_token\", \"purpose\": \"\"}],",
            StringComparison.Ordinal));

        // The purpose is what a person reads before handing over a credential. A secret that asks
        // for one without saying what it is for is asking them to guess.
        Assert.False(read.Ok);
        Assert.Contains(read.Problems, p => p.Contains("needs a purpose", StringComparison.Ordinal));

        PluginManifestRead ok = Read(Good.Replace(
            "\"required_permissions\": [\"notes.write\"],",
            "\"required_permissions\": [\"notes.write\"], "
            + "\"required_secrets\": [{\"name\": \"bot_token\", \"purpose\": \"to sign in as the bot\"}],",
            StringComparison.Ordinal));

        Assert.True(ok.Ok, string.Join("; ", ok.Problems));
        Assert.Equal("bot_token", Assert.Single(ok.Manifest!.RequiredSecrets!).Name);
    }

    [Fact]
    public void ADeclaredWindowSurvivesReading()
    {
        PluginManifestRead read = Read(WithWindow);

        Assert.Empty(read.Problems);
        SessionWindow window = read.Manifest!.Capabilities
            .Single(c => c.Key == "notes.session").OpensWindow!;

        Assert.Equal(["notes.append"], window.Actions);
        Assert.Equal(20, window.MaxActions);
        Assert.Equal(TimeSpan.FromMinutes(10), window.Lifetime);
    }

    [Fact]
    public void AWindowMayOnlyNameThisPluginsOwnActions()
    {
        // Naming a capability Aurora already has is the interesting case: a plugin that could open
        // a window over files.write_sandbox would be minting repeated authority over the host.
        PluginManifestRead read = Read(WithWindow.Replace(
            "\"actions\": [\"notes.append\"]", "\"actions\": [\"files.write_sandbox\"]",
            StringComparison.Ordinal));

        Assert.Null(read.Manifest);
        Assert.Contains(read.Problems, p => p.Contains("only cover actions declared in the manifest"));
    }

    [Fact]
    public void AWindowOverAHighRiskActionIsRefusedRatherThanPromised()
    {
        PluginManifestRead read = Read(Window(risk: "HIGH", opener: true, covered: true));

        Assert.Null(read.Manifest);
        Assert.Contains(read.Problems, p => p.Contains("never covers anything above MEDIUM"));
    }

    [Fact]
    public void AFreeCapabilityCannotOpenAWindow()
    {
        // Otherwise the authority would be minted by a call nobody was asked about.
        PluginManifestRead read = Read(Window(risk: "MEDIUM", opener: false, covered: true));

        Assert.Null(read.Manifest);
        Assert.Contains(read.Problems, p => p.Contains("a call they approved"));
    }

    [Fact]
    public void AWindowOverAFreeActionCoversNothingAndIsSaidSo()
    {
        PluginManifestRead read = Read(Window(risk: "LOW", opener: true, covered: false));

        Assert.Null(read.Manifest);
        Assert.Contains(read.Problems, p => p.Contains("covers nothing"));
    }

    [Fact]
    public void AWindowStatesItsBounds()
    {
        PluginManifestRead read = Read(WithWindow
            .Replace("\"max_actions\": 20", "\"max_actions\": 0", StringComparison.Ordinal)
            .Replace("\"lifetime_seconds\": 600", "\"lifetime_seconds\": 86400", StringComparison.Ordinal));

        Assert.Null(read.Manifest);
        Assert.Contains(read.Problems, p => p.Contains("max_actions must be between 1 and 200"));
        Assert.Contains(read.Problems, p => p.Contains("lifetime_seconds must be between 1 and 3600"));
    }
}
