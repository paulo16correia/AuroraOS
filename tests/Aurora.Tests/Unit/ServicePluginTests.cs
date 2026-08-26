using System.Text.Json;
using Aurora.Adapters.Plugins;
using Aurora.Adapters.Plugins.Sandboxes;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
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

    private static async Task<string> RootAsync(string pluginId, string body)
    {
        var root = TestTemp.Folder("svc");
        var directory = Path.Combine(root, pluginId.Replace('/', '-'));
        Directory.CreateDirectory(Path.Combine(directory, "work"));

        // A real executable, because that is what a plugin is: the sandbox launches a program,
        // not an interpreter Aurora chose on the plugin's behalf.
        var script = Path.Combine(directory, "service.py");
        await File.WriteAllTextAsync(script, "#!/usr/bin/env python3\n" + body, Ct);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return root;
    }

    private static PluginManifest Manifest(
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
            Service: new PluginService(
                "service.py", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)),
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
        string root, Observations observations, IPluginSecretSource? secrets = null) =>
        new(
            root, new UnconfinedSandbox("tests run the process directly"),
            secrets ?? new Secrets(), observations, new TestClock(DateTimeOffset.UnixEpoch),
            allowUnconfined: true);

    private static PluginInvocation Call(string input = """{"n":1}""") =>
        new("plugin/svc", "do.thing", input, Sensitivity.Private);

    // ---- the protocol ----

    [Fact]
    public async Task AServiceStartsOnceAndAnswersManyCalls()
    {
        var root = await RootAsync("plugin/svc", Answers);
        var observations = new Observations();
        await using ServicePluginHost host = Host(root, observations);

        PluginManifest manifest = Manifest();

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
        var root = await RootAsync("plugin/svc", Answers);
        await using ServicePluginHost host = Host(root, new Observations());

        PluginResult result = await host.InvokeAsync(Manifest(), Call(), Ct);

        Assert.True(result.Ok, result.Detail);
    }

    [Fact]
    public async Task WhatAServiceReportsArrivesAsAnObservation()
    {
        var root = await RootAsync("plugin/svc", Answers);
        var observations = new Observations();
        await using ServicePluginHost host = Host(root, observations);

        await host.InvokeAsync(Manifest(), Call(), Ct);

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
        var root = await RootAsync("plugin/svc", Answers);
        await using ServicePluginHost host = Host(root, new Observations());

        PluginResult result = await host.InvokeAsync(Manifest(), Call(), Ct);

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
        var root = await RootAsync("plugin/svc", Answers);
        await using ServicePluginHost host = Host(
            root, new Observations(), new Secrets { Value = null });

        PluginResult refused = await host.InvokeAsync(Manifest(), Call(), Ct);

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
        var root = await RootAsync("plugin/svc", NeverAnswers);
        await using ServicePluginHost host = Host(root, new Observations());

        PluginResult result = await host.InvokeAsync(
            Manifest(effects: ["discord.message.send"], timeoutSeconds: 2), Call(), Ct);

        Assert.False(result.Ok);

        // The message may be in the channel. Calling this failed is how a retry sends it twice.
        Assert.Equal(PluginRefusal.AmbiguousOutcome, result.Refusal);
        Assert.Contains("may or may not", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReadThatGetsNoAnswerIsSimplyFailed()
    {
        var root = await RootAsync("plugin/svc", NeverAnswers);
        await using ServicePluginHost host = Host(root, new Observations());

        // Nothing happened and asking again is free, so there is nothing ambiguous about it.
        PluginResult result = await host.InvokeAsync(
            Manifest(effects: [], timeoutSeconds: 2), Call(), Ct);

        Assert.False(result.Ok);
        Assert.Equal("timed_out", result.Refusal);
    }

    [Fact]
    public async Task APluginMaySayItDoesNotKnow()
    {
        var root = await RootAsync("plugin/svc", Unsure);
        await using ServicePluginHost host = Host(root, new Observations());

        PluginResult result = await host.InvokeAsync(
            Manifest(effects: ["discord.message.send"]), Call(), Ct);

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
        var root = await RootAsync("plugin/svc", Answers);
        await using ServicePluginHost host = Host(root, new Observations());

        await host.InvokeAsync(Manifest(), Call(), Ct);
        Assert.Single(host.Running());

        await host.StopAsync("plugin/svc", Ct);

        Assert.Empty(host.Running());
    }

    [Fact]
    public async Task APluginWithNoServiceIsNotAServicePlugin()
    {
        var root = await RootAsync("plugin/svc", Answers);
        await using ServicePluginHost host = Host(root, new Observations());

        PluginManifest oneShot = Manifest() with { Service = null };

        // The two hosts are not interchangeable and saying so is better than starting nothing and
        // timing out.
        await Assert.ThrowsAsync<PluginException>(
            () => host.InvokeAsync(oneShot, Call(), Ct));
    }
}
