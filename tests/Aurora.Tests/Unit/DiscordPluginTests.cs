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
/// The Discord plugin, run as itself against a stand-in for Discord.
/// </summary>
/// <remarks>
/// The plugin is the real file from <c>plugins/discord</c>, started as a real subprocess by the
/// real service host, building real HTTP requests. Only the far end of the socket is a fake, and
/// it records what it was asked — a send that returns success without reaching the API would
/// otherwise be indistinguishable from one that worked.
/// </remarks>
public sealed class DiscordPluginTests : IDisposable
{
    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private readonly FakeDiscord _discord = new();
    private readonly List<string> _roots = [];

    public void Dispose() => _discord.Dispose();

    /// <summary>Copies the real plugin somewhere it can be run, pointed at the fake.</summary>
    private string _executable = string.Empty;

    private string PluginRoot()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);

        while (repository is not null && !Directory.Exists(Path.Combine(repository.FullName, "plugins")))
        {
            repository = repository.Parent;
        }

        Assert.NotNull(repository);

        var root = TestTemp.Folder("discord");
        var directory = Path.Combine(root, "plugin-discord");
        Directory.CreateDirectory(Path.Combine(directory, "work"));

        var source = Path.Combine(repository!.FullName, "plugins", "discord");

        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), overwrite: true);
        }

        var executable = Path.Combine(directory, "discord_service.py");
        _executable = executable;

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // Where the stand-in is listening. The plugin checks this against the hosts its manifest
        // declares, so the test manifest below declares 127.0.0.1 and the same check runs as in
        // production.
        File.WriteAllText(
            Path.Combine(directory, "config.json"),
            JsonSerializer.Serialize(new { api_base = _discord.BaseUrl }));

        _roots.Add(root);
        return root;
    }

    /// <summary>The real manifest, with the host swapped for the stand-in's.</summary>
    private PluginManifest Manifest(params string[] endpoints)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);

        while (repository is not null && !Directory.Exists(Path.Combine(repository.FullName, "plugins")))
        {
            repository = repository.Parent;
        }

        var json = File.ReadAllText(
            Path.Combine(repository!.FullName, "plugins", "discord", "plugin.json"));

        PluginManifestRead read = PluginManifestReader.Read(json, []);
        Assert.True(read.Ok, string.Join("; ", read.Problems));

        return read.Manifest! with
        {
            Executable = _executable,
            Service = read.Manifest!.Service! with { Executable = _executable },
            NetworkEndpoints = endpoints.Length > 0 ? endpoints : ["127.0.0.1"],
        };
    }

    private sealed class Token : IPluginSecretSource
    {
        public string? Value { get; init; } = "a-bot-token-that-is-never-logged";

        public Task<string?> FindAsync(string pluginId, string name, CancellationToken ct) =>
            Task.FromResult(Value);
    }

    private sealed class Observations : IPluginObservationSink
    {
        public List<PluginObservation> Seen { get; } = [];

        public Task ReceiveAsync(PluginObservation observation, CancellationToken ct)
        {
            lock (Seen)
            {
                Seen.Add(observation);
            }

            return Task.CompletedTask;
        }
    }

    private ServicePluginHost Host(IPluginSecretSource? token = null) =>
        new(
            PluginRoot(), new UnconfinedSandbox("the sandbox is covered by its own tests"),
            token ?? new Token(), new Observations(), new TestClock(DateTimeOffset.UnixEpoch),
            allowUnconfined: true);

    private static PluginInvocation Call(
        string capability, object input, string? idempotencyKey = null) =>
        new(
            "plugin/discord", capability, JsonSerializer.Serialize(input),
            Sensitivity.Private, idempotencyKey, NetworkGranted: true);

    private static JsonNode Output(PluginResult result)
    {
        Assert.True(result.Ok, $"{result.Refusal}: {result.Detail}");
        return JsonNode.Parse(result.OutputJson!)!;
    }

    // ---- the manifest itself ----

    [Fact]
    public void TheShippedManifestIsValid()
    {
        PluginManifest manifest = Manifest();

        Assert.Equal("plugin/discord", manifest.PluginId);
        Assert.Equal(35, manifest.Capabilities.Count);
        Assert.NotNull(manifest.Service);
        Assert.Equal("bot_token", Assert.Single(manifest.RequiredSecrets!).Name);

        // Every write declares an effect, and every effectful capability needs approval. Nothing
        // that changes somebody's Discord happens because Aurora felt like it.
        foreach (PluginCapability capability in manifest.Capabilities.Where(c => c.Effects.Count > 0))
        {
            Assert.True(
                capability.ApprovalRequired,
                $"{capability.Key} has effects and does not require approval");
        }

        // HIGH is only permitted by policy when it is also reversible, so a HIGH capability that
        // is not would be denied on every call — dead on arrival rather than dangerous.
        foreach (PluginCapability capability in manifest.Capabilities.Where(c => c.Risk == RiskLevel.High))
        {
            Assert.True(capability.Reversible, $"{capability.Key} is HIGH and not reversible");
        }
    }

    [Fact]
    public void OnlyCapabilitiesThatChangeNothingSkipApproval()
    {
        // Two groups do not ask. The structural reads: which servers, channels and voice channels
        // exist, and whether Aurora is signed in or in a call. And everything that only ever
        // reduces what Aurora is doing — leaving, stopping, muting — because being unable to stop
        // is worse than stopping unexpectedly.
        //
        // Reading what people wrote is in neither group: other people's words are worth being
        // asked about. Nor is going online, which everybody can see.
        var automatic = Manifest().Capabilities
            .Where(c => !c.ApprovalRequired)
            .Select(c => c.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [
                "discord.channels.get", "discord.channels.list", "discord.gateway.status",
                "discord.guilds.get", "discord.guilds.list", "discord.threads.list",
                "discord.voice.leave", "discord.voice.list_channels", "discord.voice.mute",
                "discord.voice.pending", "discord.voice.status", "discord.voice.stop",
            ],
            automatic);
    }

    // ---- reads ----

    [Fact]
    public async Task ItListsTheServersTheBotIsIn()
    {
        _discord.Route("GET", "/users/@me/guilds", new[]
        {
            new { id = "111111111111111111", name = "Aurora Test" },
        });

        await using ServicePluginHost host = Host();

        JsonNode output = Output(
            await host.InvokeAsync(Manifest(), Call("discord.guilds.list", new { }), Ct));

        Assert.Equal("Aurora Test", output["guilds"]![0]!["name"]!.GetValue<string>());

        // The bot token went out in the header, which is the one place it belongs.
        Assert.Single(_discord.Seen);
    }

    [Fact]
    public async Task ItReadsMessagesAndKeepsWhatMatters()
    {
        _discord.Route("GET", "/channels/222222222222222222/messages", new[]
        {
            new
            {
                id = "333333333333333333",
                channel_id = "222222222222222222",
                content = "hello Aurora",
                timestamp = "2026-08-26T12:00:00+00:00",
                author = new { id = "444444444444444444", username = "paulo", bot = false },
            },
        });

        await using ServicePluginHost host = Host();

        JsonNode output = Output(await host.InvokeAsync(
            Manifest(),
            Call("discord.messages.list", new { channel_id = "222222222222222222", limit = 10 }),
            Ct));

        JsonNode message = output["messages"]![0]!;

        Assert.Equal("hello Aurora", message["content"]!.GetValue<string>());
        Assert.Equal("paulo", message["author_name"]!.GetValue<string>());
        Assert.False(message["author_is_bot"]!.GetValue<bool>());

        // The id comes back, because an observation that cannot be pointed at a message is not
        // much of an observation.
        Assert.Equal("333333333333333333", message["message_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task AChannelThatDoesNotExistIsNotFoundRatherThanFailed()
    {
        await using ServicePluginHost host = Host();

        PluginResult result = await host.InvokeAsync(
            Manifest(),
            Call("discord.channels.get", new { channel_id = "999999999999999999" }), Ct);

        // The planner needs to tell "this will never work" from "try again later".
        Assert.False(result.Ok);
        Assert.Equal("not_found", result.Refusal);
    }

    // ---- writes ----

    [Fact]
    public async Task ItSendsAMessageAndReturnsWhatWasCreated()
    {
        _discord.Route("POST", "/channels/222222222222222222/messages", request =>
        {
            var sent = request.Json!;
            return (200, JsonSerializer.Serialize(new
            {
                id = "555555555555555555",
                channel_id = "222222222222222222",
                content = sent["content"]!.GetValue<string>(),
                nonce = sent["nonce"]?.GetValue<string>(),
                timestamp = "2026-08-26T12:01:00+00:00",
                author = new { id = "666666666666666666", username = "aurora", bot = true },
            }));
        });

        await using ServicePluginHost host = Host();

        JsonNode output = Output(await host.InvokeAsync(
            Manifest(),
            Call(
                "discord.messages.send",
                new { channel_id = "222222222222222222", content = "hello from Aurora" },
                idempotencyKey: "key-1"),
            Ct));

        // The observation carries what a later call would need: which message, in which channel.
        Assert.Equal("555555555555555555", output["message_id"]!.GetValue<string>());
        Assert.Equal("222222222222222222", output["channel_id"]!.GetValue<string>());

        Assert.Equal(1, _discord.Posted);
    }

    [Fact]
    public async Task EditingReturnsWhatItSaidBeforeSoItCanBePutBack()
    {
        _discord.Route("GET", "/channels/222222222222222222/messages/555555555555555555", new
        {
            id = "555555555555555555", channel_id = "222222222222222222",
            content = "the original text", author = new { id = "6", username = "aurora", bot = true },
        });

        _discord.Route("PATCH", "/channels/222222222222222222/messages/555555555555555555", new
        {
            id = "555555555555555555", channel_id = "222222222222222222",
            content = "the corrected text", author = new { id = "6", username = "aurora", bot = true },
        });

        await using ServicePluginHost host = Host();

        JsonNode output = Output(await host.InvokeAsync(
            Manifest(),
            Call("discord.messages.edit", new
            {
                channel_id = "222222222222222222",
                message_id = "555555555555555555",
                content = "the corrected text",
            }),
            Ct));

        // The manifest calls this reversible, and this is what pays for the claim: the caller is
        // given what it needs to undo the change.
        Assert.Equal("the original text", output["previous_content"]!.GetValue<string>());
    }

    [Fact]
    public async Task CreatingAChannelReturnsItsIdSoItCanBeRemoved()
    {
        _discord.Route("POST", "/guilds/111111111111111111/channels", new
        {
            id = "777777777777777777", name = "aurora-notes", type = 0,
        });

        await using ServicePluginHost host = Host();

        JsonNode output = Output(await host.InvokeAsync(
            Manifest(),
            Call("discord.channels.create", new
            {
                guild_id = "111111111111111111", name = "aurora-notes", type = "text",
            }),
            Ct));

        // HIGH risk is only permitted when reversible, and for a creation that means handing back
        // the id of the thing created.
        Assert.Equal("777777777777777777", output["channel_id"]!.GetValue<string>());
    }

    // ---- the outcome nobody knows, and not sending twice ----

    [Fact]
    public async Task ASendThatGetsNoAnswerIsResolvedByLookingRatherThanBySendingAgain()
    {
        var attempts = 0;

        _discord.Route("POST", "/channels/222222222222222222/messages", request =>
        {
            attempts++;

            // Discord had the request and the answer never came back. From here that is
            // indistinguishable from it never arriving — which is the whole problem.
            return (500, """{"message":"Internal Server Error"}""");
        });

        _discord.Route("GET", "/channels/222222222222222222/messages", _ => (200,
            JsonSerializer.Serialize(new[]
            {
                new
                {
                    id = "888888888888888888", channel_id = "222222222222222222",
                    content = "hello from Aurora", nonce = "key-dup",
                    author = new { id = "6", username = "aurora", bot = true },
                },
            })));

        await using ServicePluginHost host = Host();

        JsonNode output = Output(await host.InvokeAsync(
            Manifest(),
            Call(
                "discord.messages.send",
                new { channel_id = "222222222222222222", content = "hello from Aurora" },
                idempotencyKey: "key-dup"),
            Ct));

        // It did land. Discord has no idempotency key for message creation, but it echoes the
        // nonce back — so a send that got no answer is resolved by reading the channel and
        // looking for our own, rather than by posting again and hoping.
        Assert.Equal("888888888888888888", output["message_id"]!.GetValue<string>());
        Assert.Equal(1, attempts);
        Assert.Equal(1, _discord.Posted);
    }

    [Fact]
    public async Task ASendThatCannotBeConfirmedIsUnknownAndNotRetried()
    {
        _discord.Route("POST", "/channels/222222222222222222/messages",
            _ => (500, """{"message":"Internal Server Error"}"""));

        // The channel does not show it. That is not proof it was not sent — Discord may still be
        // processing — so the answer is "unknown", never "failed".
        _discord.Route("GET", "/channels/222222222222222222/messages", _ => (200, "[]"));

        await using ServicePluginHost host = Host();

        PluginResult result = await host.InvokeAsync(
            Manifest(),
            Call(
                "discord.messages.send",
                new { channel_id = "222222222222222222", content = "did this send?" },
                idempotencyKey: "key-lost"),
            Ct);

        Assert.False(result.Ok);
        Assert.Equal(PluginRefusal.AmbiguousOutcome, result.Refusal);

        // Exactly one attempt. An automatic retry here is how somebody gets the same message twice.
        Assert.Equal(1, _discord.Posted);
    }

    // ---- rate limits ----

    [Fact]
    public async Task ARateLimitComesBackAsSomethingTheSchedulerCanUse()
    {
        _discord.Route("POST", "/channels/222222222222222222/messages",
            _ => (429, """{"message":"You are being rate limited.","retry_after":4.5}"""));

        _discord.Route("GET", "/channels/222222222222222222/messages", _ => (200, "[]"));

        await using ServicePluginHost host = Host();

        PluginResult result = await host.InvokeAsync(
            Manifest(),
            Call(
                "discord.messages.send",
                new { channel_id = "222222222222222222", content = "too fast" },
                idempotencyKey: "key-429"),
            Ct);

        Assert.False(result.Ok);

        // Named, and carrying when to come back. "Failed" would tell the planner nothing it could
        // act on, and a blocking sleep would hold a kernel thread for somebody else's quota.
        Assert.Equal("rate_limited", result.Refusal);
    }

    // ---- the token ----

    [Fact]
    public async Task TheTokenIsUsedAndNeverComesBack()
    {
        _discord.Route("GET", "/users/@me/guilds", new[] { new { id = "1", name = "g" } });

        await using ServicePluginHost host = Host();

        PluginResult result = await host.InvokeAsync(
            Manifest(), Call("discord.guilds.list", new { }), Ct);

        Assert.True(result.Ok, result.Detail);

        // Used: the fake refuses anything without a bot token, so a success proves it was sent.
        // And absent from everything that comes back or is kept.
        const string Token = "a-bot-token-that-is-never-logged";

        Assert.DoesNotContain(Token, result.OutputJson ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(Token, result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Token, string.Join(" ", host.Running().Select(state => state.Detail ?? "")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutATokenNothingIsAttempted()
    {
        await using ServicePluginHost host = Host(new Token { Value = null });

        PluginResult result = await host.InvokeAsync(
            Manifest(), Call("discord.guilds.list", new { }), Ct);

        Assert.False(result.Ok);
        Assert.Equal(PluginRefusal.ServiceUnavailable, result.Refusal);

        // Not one request went out. A plugin started without its credential would have produced a
        // 401 that reads like Discord rejecting the bot.
        Assert.Empty(_discord.Seen);
    }

    [Fact]
    public async Task AnInvalidTokenIsSaidPlainlyAndIsNotAFailureToRetry()
    {
        _discord.Route("GET", "/users/@me/guilds", _ => (401, """{"message":"401: Unauthorized"}"""));

        await using ServicePluginHost host = Host();

        PluginResult result = await host.InvokeAsync(
            Manifest(), Call("discord.guilds.list", new { }), Ct);

        Assert.False(result.Ok);

        // Distinct from a network failure, because retrying will never fix it and a person has to
        // do something.
        Assert.Equal("invalid_credentials", result.Refusal);
    }

    // ---- the plugin holds itself to the hosts it declared ----

    [Fact]
    public async Task ThePluginRefusesToTalkToAHostItsManifestDoesNotName()
    {
        await using ServicePluginHost host = Host();

        // The config points at the stand-in on 127.0.0.1 and the manifest here names only
        // discord.com. Aurora cannot enforce this — no sandbox filters by hostname — so the plugin
        // enforces it on itself, being the only party that knows the name it is about to resolve.
        PluginResult result = await host.InvokeAsync(
            Manifest("discord.com"), Call("discord.guilds.list", new { }), Ct);

        Assert.False(result.Ok);
        Assert.Empty(_discord.Seen);
    }
}
