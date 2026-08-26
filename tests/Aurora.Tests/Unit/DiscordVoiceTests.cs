using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora.Adapters.Plugins;
using Aurora.Adapters.Plugins.Sandboxes;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Voice: what it refuses, what it never gates, and the turn-taking rules underneath.
/// </summary>
/// <remarks>
/// The audio transport itself is UNVERIFIED and says so — see <c>docs/adr/0068</c>. What is
/// verified here is everything that decides whether audio should move at all, which is where being
/// wrong costs something: talking over people, hearing itself, being unable to stop.
/// </remarks>
public sealed class DiscordVoiceTests : IDisposable
{
    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;

    private readonly FakeDiscord _discord = new();

    public void Dispose() => _discord.Dispose();

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
        Path.Combine(Repository().FullName, "plugins", "discord");

    // ---- the turn-taking rules, tested where they live ----

    [Fact]
    public void TheTurnTakingRulesHold()
    {
        // The state machine is Python, so its tests are Python. Run from here so the suite is one
        // place to look: a rule about not talking over people is not less important for being
        // written in another language.
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
        python.StartInfo.ArgumentList.Add("test_voice");
        python.StartInfo.ArgumentList.Add("-v");

        python.Start();
        var output = python.StandardOutput.ReadToEnd() + python.StandardError.ReadToEnd();
        python.WaitForExit(60_000);

        Assert.True(python.ExitCode == 0, output);

        // The count is asserted so a file that silently stops being collected fails here rather
        // than passing with nothing run.
        Assert.Contains("Ran 13 tests", output, StringComparison.Ordinal);
    }

    // ---- the manifest and the program agree ----

    [Fact]
    public void EveryDeclaredCapabilityIsHandledAndEveryHandlerIsDeclared()
    {
        var json = File.ReadAllText(Path.Combine(PluginSource(), "plugin.json"));
        PluginManifestRead read = PluginManifestReader.Read(json, []);
        Assert.True(read.Ok, string.Join("; ", read.Problems));

        var declared = read.Manifest!.Capabilities
            .Select(c => c.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        // Asked of the program itself rather than of a list beside it. A manifest that promises a
        // capability nothing implements is a catalogue entry that fails on first use, and a
        // handler nobody declared is code Aurora will never route to.
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

        python.StartInfo.ArgumentList.Add("-c");
        python.StartInfo.ArgumentList.Add(
            "import json, discord_service as d; "
            + "print(json.dumps(sorted(set(d.READS) | set(d.WRITES) "
            + "| set(d.GATEWAY_READS) | set(d.GATEWAY_WRITES))))");

        python.Start();
        var output = python.StandardOutput.ReadToEnd();
        var errors = python.StandardError.ReadToEnd();
        python.WaitForExit(30_000);

        Assert.True(python.ExitCode == 0, errors);

        var handled = JsonSerializer.Deserialize<List<string>>(output.Trim())!;

        Assert.Equal(declared, handled);
    }

    // ---- stopping is never gated ----

    [Fact]
    public void EverythingThatOnlyReducesWhatAuroraDoesIsUngated()
    {
        var json = File.ReadAllText(Path.Combine(PluginSource(), "plugin.json"));
        PluginManifest manifest = PluginManifestReader.Read(json, []).Manifest!;

        foreach (var key in new[]
        {
            "discord.voice.leave", "discord.voice.stop", "discord.voice.mute",
        })
        {
            PluginCapability capability = manifest.Capabilities.Single(c => c.Key == key);

            // Being unable to leave is worse than leaving unexpectedly. If stopping needed an
            // approval, Aurora could be held in somebody's call by nobody being at the keyboard —
            // so these declare no effect and ask nobody, exactly like disabling a plugin.
            Assert.Equal(RiskLevel.Low, capability.Risk);
            Assert.False(capability.ApprovalRequired, $"{key} must never wait for an approval");
            Assert.Empty(capability.Effects);
        }

        foreach (var key in new[]
        {
            "discord.voice.join", "discord.voice.speak", "discord.voice.listen",
            "discord.voice.unmute",
        })
        {
            PluginCapability capability = manifest.Capabilities.Single(c => c.Key == key);

            // And everything that extends what Aurora is doing asks first.
            Assert.True(capability.ApprovalRequired, $"{key} must require approval");
            Assert.NotEmpty(capability.Effects);
        }
    }

    // ---- what this machine can actually do, said out loud ----

    [Fact]
    public async Task VoiceReportsWhatIsMissingRatherThanFailingLater()
    {
        var root = TestTemp.Folder("discord-voice");
        var directory = Path.Combine(root, "plugin-discord");
        Directory.CreateDirectory(Path.Combine(directory, "work"));

        foreach (var file in Directory.EnumerateFiles(PluginSource()))
        {
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), overwrite: true);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                Path.Combine(directory, "discord_service.py"),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        File.WriteAllText(
            Path.Combine(directory, "config.json"),
            JsonSerializer.Serialize(new { api_base = _discord.BaseUrl }));

        var json = File.ReadAllText(Path.Combine(PluginSource(), "plugin.json"));
        PluginManifest manifest = PluginManifestReader.Read(json, []).Manifest! with
        {
            NetworkEndpoints = ["127.0.0.1"],
        };

        await using var host = new ServicePluginHost(
            root, new UnconfinedSandbox("the sandbox is covered by its own tests"),
            new Token(), new Sink(), new TestClock(DateTimeOffset.UnixEpoch),
            allowUnconfined: true);

        PluginResult status = await host.InvokeAsync(
            manifest,
            new PluginInvocation(
                "plugin/discord", "discord.voice.status", "{}", Sensitivity.Private, null,
                NetworkGranted: true),
            Ct);

        Assert.True(status.Ok, status.Detail);

        JsonNode capabilities = JsonNode.Parse(status.OutputJson!)!["capabilities"]!;

        // The property that would be quietly lost first, asserted rather than assumed. There is no
        // fallback in this plugin that reaches a speech service: if nothing local is installed the
        // capability refuses and says what to install.
        Assert.False(capabilities["audio_leaves_this_machine"]!.GetValue<bool>());

        // And whatever this machine is missing is named, so somebody deciding whether to have
        // Aurora join a call finds out first rather than in the middle of a conversation.
        foreach (var flag in new[] { "can_join", "can_listen", "can_speak" })
        {
            Assert.True(capabilities[flag] is not null, $"{flag} is not reported");
        }

        if (!capabilities["can_listen"]!.GetValue<bool>())
        {
            Assert.NotEmpty(capabilities["missing"]!.AsArray());
        }
    }

    [Fact]
    public async Task JoiningACallWithoutBeingSignedInIsRefusedRatherThanAttempted()
    {
        var root = TestTemp.Folder("discord-voice-2");
        var directory = Path.Combine(root, "plugin-discord");
        Directory.CreateDirectory(Path.Combine(directory, "work"));

        foreach (var file in Directory.EnumerateFiles(PluginSource()))
        {
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), overwrite: true);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                Path.Combine(directory, "discord_service.py"),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        File.WriteAllText(
            Path.Combine(directory, "config.json"),
            JsonSerializer.Serialize(new { api_base = _discord.BaseUrl }));

        var json = File.ReadAllText(Path.Combine(PluginSource(), "plugin.json"));
        PluginManifest manifest = PluginManifestReader.Read(json, []).Manifest! with
        {
            NetworkEndpoints = ["127.0.0.1"],
        };

        await using var host = new ServicePluginHost(
            root, new UnconfinedSandbox("the sandbox is covered by its own tests"),
            new Token(), new Sink(), new TestClock(DateTimeOffset.UnixEpoch),
            allowUnconfined: true);

        PluginResult result = await host.InvokeAsync(
            manifest,
            new PluginInvocation(
                "plugin/discord", "discord.voice.join",
                """{"guild_id":"111111111111111111","channel_id":"222222222222222222"}""",
                Sensitivity.Private, null, NetworkGranted: true),
            Ct);

        // Joining a call Aurora cannot hear or be heard in puts a silent presence in somebody's
        // conversation and reads like a bug rather than a missing dependency.
        Assert.False(result.Ok);
        Assert.Contains(
            result.Refusal ?? "", new[] { "voice_unavailable", "network_failure" });
    }

    private sealed class Token : IPluginSecretSource
    {
        public Task<string?> FindAsync(string pluginId, string name, CancellationToken ct) =>
            Task.FromResult<string?>("a-bot-token-that-is-never-logged");
    }

    private sealed class Sink : IPluginObservationSink
    {
        public Task ReceiveAsync(PluginObservation observation, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
