using System.Text.Json;
using Aurora.Adapters.Plugins;
using Aurora.Adapters.Plugins.Sandboxes;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Server;
using Aurora.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// The long-lived plugin, exercised as a real process rather than described.
/// </summary>
/// <remarks>
/// Every test here starts an actual Python program under the real sandbox and talks to it over
/// real pipes. Faking the process would test the shape of the code and none of the things that go
/// wrong with subprocesses — a plugin that never answers, one that dies mid-call, one that writes
/// a stray line to stdout — which are the reasons this class exists (docs/adr/0067).
/// </remarks>
public sealed class ServicePluginTests
{
    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    /// <summary>A service plugin that answers, in as few lines as the protocol allows.</summary>
    private const string Answers = """
        import json, sys
        for line in sys.stdin:
            try:
                frame = json.loads(line)
            except ValueError:
                continue
            kind = frame.get("kind")
            if kind == "hello":
                # Prove the secret arrived without ever printing it: report its length.
                secret = frame.get("secrets", {}).get("token", "")
                print(json.dumps({"kind": "ready", "secret_len": len(secret)}), flush=True)
                print(json.dumps({
                    "kind": "event", "type": "connected",
                    "payload": {"guild": "g1"}}), flush=True)
            elif kind == "call":
                print("a stray line that is not json", flush=True)
                print(json.dumps({
                    "kind": "result", "id": frame["id"], "ok": True,
                    "output": {"echo": frame.get("input", {}), "cap": frame["capability"]}}),
                    flush=True)
            elif kind == "shutdown":
                break
        """;

    /// <summary>One that goes quiet: it reads, reports ready, and never answers a call.</summary>
    private const string NeverAnswers = """
        import json, sys
        for line in sys.stdin:
            frame = json.loads(line)
            if frame.get("kind") == "hello":
                print(json.dumps({"kind": "ready"}), flush=True)
        """;

    /// <summary>One that says it does not know what happened.</summary>
    private const string Unsure = """
        import json, sys
        for line in sys.stdin:
            frame = json.loads(line)
            if frame.get("kind") == "hello":
                print(json.dumps({"kind": "ready"}), flush=True)
            elif frame.get("kind") == "call":
                print(json.dumps({
                    "kind": "result", "id": frame["id"], "ok": False,
                    "outcome": "unknown", "detail": "the send timed out"}), flush=True)
        """;

    /// <summary>One that writes to stderr the way a real program does — logs, warnings, tracebacks.</summary>
    private const string Noisy = """
        import json, sys
        for line in sys.stdin:
            frame = json.loads(line)
            if frame.get("kind") == "hello":
                print(json.dumps({"kind": "ready"}), flush=True)
            elif frame.get("kind") == "call":
                # A chatty library. 200KB is nothing for a debug log and more than a pipe holds.
                for i in range(4000):
                    sys.stderr.write("debug: reconnecting to the gateway, attempt %d\n" % i)
                sys.stderr.flush()
                print(json.dumps({
                    "kind": "result", "id": frame["id"], "ok": True, "output": {}}), flush=True)
        """;

    /// <summary>Where a plugin's files live and where Aurora lets it work.</summary>
    private sealed record Installed(string Root, string Executable);

    private static async Task<Installed> RootAsync(string pluginId, string body)
    {
        var root = TestTemp.Folder("svc");

        // Where the plugin's files actually live, which is wherever its author put them — the
        // manifest carries the path, sealed at install. Not under the plugin root: that is only
        // the working directory Aurora hands it.
        var directory = TestTemp.Folder("svc-files");

        // A real executable, because that is what a plugin is: the sandbox launches a program,
        // not an interpreter Aurora chose on the plugin's behalf.
        var script = Path.Combine(directory, "service.py");
        await File.WriteAllTextAsync(script, "#!/usr/bin/env python3\n" + body, Ct);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return new Installed(root, script);
    }

    private static PluginManifest Manifest(
        string executable,
        string pluginId = "plugin/svc", string[]? effects = null, int timeoutSeconds = 3) =>
        new(
            pluginId, "1.0.0", "acme", "", MinPlatformVersion: 1,
            Capabilities:
            [
                new PluginCapability(
                    "do.thing", "{}", "{}", effects ?? [], ApprovalRequired: false,
                    RateLimitPerMinute: 60, Timeout: TimeSpan.FromSeconds(timeoutSeconds),
                    IdempotencySupport: true, AuditLevel: "FULL"),
            ],
            EventSubscriptions: [], RequiredPermissions: [],
            MaxDataClass: Sensitivity.Private, NetworkEndpoints: [],
            DocumentationRef: "docs", IntegrityHash: "",
            Executable: executable,
            Service: new PluginService(executable, TimeSpan.FromSeconds(10)),
            RequiredSecrets: [new PluginSecretRequirement("token", "the bot token")]);

    private sealed class Secrets : IPluginSecretSource
    {
        public string? Value { get; init; } = "s3cr3t-token-value";

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

    private static ServicePluginHost Host(
        string root, Observations observations, IPluginSecretSource? secrets = null,
        TestClock? clock = null) =>
        new(
            root, new UnconfinedSandbox("tests run the process directly"),
            secrets ?? new Secrets(), observations,
            clock ?? new TestClock(DateTimeOffset.UnixEpoch),
            allowUnconfined: true);

    private static PluginInvocation Call(string input = """{"n":1}""") =>
        new("plugin/svc", "do.thing", input, Sensitivity.Private);

    // ---- the protocol ----

    [Fact]
    public async Task AServiceStartsOnceAndAnswersManyCalls()
    {
        Installed installed = await RootAsync("plugin/svc", Answers);
        var observations = new Observations();
        await using ServicePluginHost host = Host(installed.Root, observations);

        PluginManifest manifest = Manifest(installed.Executable);

        PluginResult first = await host.InvokeAsync(manifest, Call("""{"n":1}"""), Ct);
        PluginResult second = await host.InvokeAsync(manifest, Call("""{"n":2}"""), Ct);

        Assert.True(first.Ok, first.Detail);
        Assert.True(second.Ok, second.Detail);

        // Both answered, and each got its own answer rather than the other's.
        Assert.Contains("\"n\":1", first.OutputJson!, StringComparison.Ordinal);
        Assert.Contains("\"n\":2", second.OutputJson!, StringComparison.Ordinal);

        // One process for both. A service that restarted per call would be the one-shot host with
        // extra steps, and would drop the connection it exists to hold.
        Assert.Single(host.Running());
        Assert.Equal(PluginServiceStatus.Ready, host.Running()[0].Status);
    }

    [Fact]
    public async Task AStrayLineOnStdoutDoesNotBreakTheConversation()
    {
        // The answering plugin prints a non-JSON line before every result. An interpreter warning
        // or a forgotten print is normal, and must not desynchronise the stream.
        Installed installed = await RootAsync("plugin/svc", Answers);
        await using ServicePluginHost host = Host(installed.Root, new Observations());

        PluginResult result = await host.InvokeAsync(Manifest(installed.Executable), Call(), Ct);

        Assert.True(result.Ok, result.Detail);
    }

    [Fact]
    public async Task WhatAServiceReportsArrivesAsAnObservation()
    {
        Installed installed = await RootAsync("plugin/svc", Answers);
        var observations = new Observations();
        await using ServicePluginHost host = Host(installed.Root, observations);

        await host.InvokeAsync(Manifest(installed.Executable), Call(), Ct);

        // The plugin volunteered this; nothing asked for it.
        PluginObservation reported = Assert.Single(observations.Seen);
        Assert.Equal("connected", reported.Kind);
        Assert.Equal("plugin/svc", reported.PluginId);
        Assert.Contains("g1", reported.PayloadJson, StringComparison.Ordinal);
    }

    // ---- secrets ----

    [Fact]
    public async Task TheSecretReachesThePluginAndNothingElse()
    {
        Installed installed = await RootAsync("plugin/svc", Answers);
        await using ServicePluginHost host = Host(installed.Root, new Observations());

        PluginResult result = await host.InvokeAsync(Manifest(installed.Executable), Call(), Ct);

        Assert.True(result.Ok, result.Detail);

        // The plugin reported the secret's length in its ready frame, which is how this knows the
        // value arrived without the test ever holding it. And the value is in none of what comes
        // back: not the result, not the detail, not the state.
        Assert.DoesNotContain("s3cr3t", result.OutputJson ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "s3cr3t", string.Join(" ", host.Running().Select(s => s.Detail ?? "")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServiceWhoseSecretIsMissingIsNeverStarted()
    {
        Installed installed = await RootAsync("plugin/svc", Answers);
        await using ServicePluginHost host = Host(
            installed.Root, new Observations(), new Secrets { Value = null });

        PluginResult refused = await host.InvokeAsync(Manifest(installed.Executable), Call(), Ct);

        Assert.False(refused.Ok);
        Assert.Equal(PluginRefusal.ServiceUnavailable, refused.Refusal);
        Assert.Contains(PluginRefusal.SecretMissing, refused.Detail, StringComparison.Ordinal);

        // Starting it would waste a process and produce a failure that reads like a broken plugin
        // rather than a missing credential.
        Assert.Equal(PluginServiceStatus.Failed, host.Running()[0].Status);
    }

    // ---- the outcome nobody knows ----

    [Fact]
    public async Task AWriteThatGetsNoAnswerIsUnknownRatherThanFailed()
    {
        Installed installed = await RootAsync("plugin/svc", NeverAnswers);
        await using ServicePluginHost host = Host(installed.Root, new Observations());

        PluginResult result = await host.InvokeAsync(
            Manifest(installed.Executable, effects: ["discord.message.send"], timeoutSeconds: 2), Call(), Ct);

        Assert.False(result.Ok);

        // The message may be in the channel. Calling this failed is how a retry sends it twice.
        Assert.Equal(PluginRefusal.AmbiguousOutcome, result.Refusal);
        Assert.Contains("may or may not", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReadThatGetsNoAnswerIsSimplyFailed()
    {
        Installed installed = await RootAsync("plugin/svc", NeverAnswers);
        await using ServicePluginHost host = Host(installed.Root, new Observations());

        // Nothing happened and asking again is free, so there is nothing ambiguous about it.
        PluginResult result = await host.InvokeAsync(
            Manifest(installed.Executable, effects: [], timeoutSeconds: 2), Call(), Ct);

        Assert.False(result.Ok);
        Assert.Equal("timed_out", result.Refusal);
    }

    [Fact]
    public async Task APluginMaySayItDoesNotKnow()
    {
        Installed installed = await RootAsync("plugin/svc", Unsure);
        await using ServicePluginHost host = Host(installed.Root, new Observations());

        PluginResult result = await host.InvokeAsync(
            Manifest(installed.Executable, effects: ["discord.message.send"]), Call(), Ct);

        // Refusing to hear this would push authors towards guessing, and a guess here is a
        // duplicate message or a lost one.
        Assert.False(result.Ok);
        Assert.Equal(PluginRefusal.AmbiguousOutcome, result.Refusal);
        Assert.Contains("timed out", result.Detail, StringComparison.Ordinal);
    }

    // ---- lifecycle ----

    [Fact]
    public async Task StoppingAServiceEndsTheProcess()
    {
        Installed installed = await RootAsync("plugin/svc", Answers);
        await using ServicePluginHost host = Host(installed.Root, new Observations());

        await host.InvokeAsync(Manifest(installed.Executable), Call(), Ct);
        Assert.Single(host.Running());

        await host.StopAsync("plugin/svc", Ct);

        Assert.Empty(host.Running());
    }

    [Fact]
    public async Task APluginWithNoServiceIsNotAServicePlugin()
    {
        Installed installed = await RootAsync("plugin/svc", Answers);
        await using ServicePluginHost host = Host(installed.Root, new Observations());

        PluginManifest oneShot = Manifest(installed.Executable) with { Service = null };

        // The two hosts are not interchangeable and saying so is better than starting nothing and
        // timing out.
        await Assert.ThrowsAsync<PluginException>(
            () => host.InvokeAsync(oneShot, Call(), Ct));
    }

    [Fact]
    public async Task APluginThatWritesToStderrIsNotStuckBehindIt()
    {
        Installed installed = await RootAsync("plugin/svc", Noisy);
        await using ServicePluginHost host = Host(installed.Root, new Observations());

        // stderr is redirected, so if nothing drains it the pipe fills at about 64KB and the
        // plugin blocks in the middle of a write it will never finish. From Aurora's side that
        // looks exactly like a plugin that stopped answering, which is the worst kind of bug:
        // it only happens to plugins that log, and only once they have logged enough.
        PluginResult result = await host.InvokeAsync(
            Manifest(installed.Executable, timeoutSeconds: 10), Call(), Ct);

        Assert.True(result.Ok, result.Detail);
    }

    // ---- what happens when it will not start ----

    [Fact]
    public async Task AServiceThatWillNotStartIsNotStartedAgainOnEveryCall()
    {
        // The manifest names a program that is not there, which is what a broken install looks
        // like from the host's side.
        var root = TestTemp.Folder("svc-missing");
        PluginManifest manifest = Manifest(Path.Combine(root, "not-installed.py"));

        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        await using ServicePluginHost host = Host(root, new Observations(), clock: clock);

        PluginResult first = await host.InvokeAsync(manifest, Call(), Ct);
        Assert.False(first.Ok);
        Assert.Equal(PluginRefusal.ServiceUnavailable, first.Refusal);

        // The second call inside the backoff window does not spawn anything. Without this a
        // service that dies on startup is restarted once per call, which turns a broken plugin
        // into a process storm and buries the real failure under its own retries.
        PluginResult second = await host.InvokeAsync(manifest, Call(), Ct);

        Assert.False(second.Ok);
        Assert.Contains("before starting again", second.Detail, StringComparison.Ordinal);

        // And past the window it is willing to try once more.
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        PluginResult third = await host.InvokeAsync(manifest, Call(), Ct);

        Assert.DoesNotContain("before starting again", third.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FixingWhatWasBrokenTakesEffectOnTheNextCall()
    {
        Installed installed = await RootAsync("plugin/svc", Answers);
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var secrets = new Secrets { Value = null };

        await using ServicePluginHost host = Host(installed.Root, new Observations(), secrets, clock);

        PluginResult refused = await host.InvokeAsync(Manifest(installed.Executable), Call(), Ct);
        Assert.False(refused.Ok);
        Assert.Contains(PluginRefusal.SecretMissing, refused.Detail, StringComparison.Ordinal);

        // The owner supplies the credential. Stopping is what the console does after a change like
        // this, and it must clear the penalty earned by the old configuration — making somebody
        // wait out a backoff for having fixed the problem is punishing the fix.
        await host.StopAsync("plugin/svc", Ct);

        await using ServicePluginHost restarted = Host(installed.Root, new Observations(), clock: clock);
        PluginResult ok = await restarted.InvokeAsync(Manifest(installed.Executable), Call(), Ct);

        Assert.True(ok.Ok, ok.Detail);
    }

    [Fact]
    public async Task AnAnswerInTheWrongShapeRefusesOneCallRatherThanTheConnection()
    {
        Installed installed = await RootAsync("plugin/svc", """
            import json, sys
            calls = []
            for line in sys.stdin:
                frame = json.loads(line)
                if frame.get("kind") == "hello":
                    print(json.dumps({"kind": "ready"}), flush=True)
                elif frame.get("kind") == "call":
                    calls.append(1)
                    if len(calls) == 1:
                        # "ok" is supposed to be a boolean.
                        print(json.dumps({
                            "kind": "result", "id": frame["id"], "ok": "yes"}), flush=True)
                    else:
                        print(json.dumps({
                            "kind": "result", "id": frame["id"], "ok": True,
                            "output": {"second": True}}), flush=True)
            """);

        await using ServicePluginHost host = Host(installed.Root, new Observations());

        PluginResult wrong = await host.InvokeAsync(Manifest(installed.Executable), Call(), Ct);
        Assert.False(wrong.Ok);

        // The connection survives. A plugin being wrong should cost one call, not every other call
        // in flight over the socket it was holding.
        PluginResult after = await host.InvokeAsync(Manifest(installed.Executable), Call(), Ct);
        Assert.True(after.Ok, after.Detail);
        Assert.Contains("second", after.OutputJson!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRunningServerRoutesServicePluginsToTheHostThatCanRunThem()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aurora:BearerToken"] = "a-token-long-enough-to-pass-validation",
                ["Aurora:DbPath"] = TestTemp.Path("routing") + ".db",
                ["Aurora:SandboxRoot"] = TestTemp.Folder("routing-sandbox"),
            })
            .Build();

        AuroraServerOptions options = AuroraServerOptions.FromConfiguration(config);

        var collection = new ServiceCollection();
        collection.AddAurora(options);

        using ServiceProvider provider = collection.BuildServiceProvider();

        // The service host was built, tested and never registered. Nothing failed to compile and
        // no test noticed, because every test of it constructed one directly — so the running
        // server held only the one-shot host, and a service plugin would have had its stdin closed
        // underneath it and failed every call for a reason that had nothing to do with the plugin.
        var host = provider.GetRequiredService<IPluginHost>();
        Assert.IsType<RoutingPluginHost>(host);

        Assert.NotNull(provider.GetService<IPluginServiceSupervisor>());
        Assert.NotNull(provider.GetService<IPluginSecretSource>());
        Assert.NotNull(provider.GetService<IPluginObservationSink>());
    }

    [Fact]
    public async Task AnObservationThatCannotBePublishedIsNotLostSilently()
    {
        Installed installed = await RootAsync("plugin/svc", Answers);

        await using ServicePluginHost host = new(
            installed.Root, new UnconfinedSandbox("tests run the process directly"),
            new Secrets(), new RefusingSink(), new TestClock(DateTimeOffset.UnixEpoch),
            allowUnconfined: true);

        PluginResult result = await host.InvokeAsync(
            Manifest(installed.Executable), Call(), Ct);

        Assert.True(result.Ok, result.Detail);

        // The call still succeeds — a report failing to land is not a reason to lose the
        // connection it came in on. But it must leave a mark. A silent catch around the only path
        // a plugin has into Aurora is a path that can stop working without anybody noticing, and
        // it did: every observation the Discord plugin produced vanished because the event
        // contract was refusing them and nothing said a word.
        for (var waited = 0; waited < 50 && host.Running()[0].Detail is null; waited++)
        {
            await Task.Delay(100, Ct);
        }

        Assert.Contains(
            "was not published", host.Running()[0].Detail ?? "", StringComparison.Ordinal);
    }

    private sealed class RefusingSink : IPluginObservationSink
    {
        public Task ReceiveAsync(PluginObservation observation, CancellationToken ct) =>
            throw new InvalidOperationException("the contract refused this event");
    }

    [Fact]
    public async Task WhatTheInvocationWasGrantedReachesTheSandbox()
    {
        Installed installed = await RootAsync("plugin/svc", Answers);
        var sandbox = new RecordingSandbox();

        await using ServicePluginHost host = new(
            installed.Root, sandbox, new Secrets(), new Observations(),
            new TestClock(DateTimeOffset.UnixEpoch), allowUnconfined: true);

        await host.InvokeAsync(
            Manifest(installed.Executable),
            Call() with { NetworkGranted = true, GpuGranted = true },
            Ct);

        // Stored, asked for, refused when absent — and then not passed to the thing that enforces
        // it. Both grants were wired through the contracts and neither reached the sandbox from
        // this host, so a plugin the owner had granted the GPU still crashed on Metal. Nothing
        // failed to compile and no test noticed, because every test of the profile built a request
        // by hand.
        Assert.NotNull(sandbox.Last);
        Assert.True(sandbox.Last!.NetworkGranted, "the network grant did not reach the sandbox");
        Assert.True(sandbox.Last!.GpuGranted, "the GPU grant did not reach the sandbox");
    }

    private sealed class RecordingSandbox : WrapperSandbox
    {
        public SandboxRequest? Last { get; private set; }

        public override SandboxPlan Plan(SandboxRequest request)
        {
            Last = request;
            return new UnconfinedSandbox("recording what it was asked").Plan(request);
        }
    }
}
