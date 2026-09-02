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
/// The Microsoft plugin started by Aurora's own plugin host, rather than by a Python test.
/// </summary>
/// <remarks>
/// <b>Why this file exists.</b> A completion audit found that every Microsoft test ran the plugin's
/// modules directly — which proves the code is right and proves nothing about whether Aurora can
/// start it, speak its protocol to it, or get an answer back. Discord had that coverage; Microsoft
/// did not, so its runtime path had never executed once.
/// <para>
/// The seam that made it impossible is worth recording. The plugin read the stand-in's address from
/// an environment variable, and both plugin hosts call <c>Environment.Clear()</c> before launching —
/// so a test using that seam could only ever run the plugin standalone. It now reads
/// <c>config.json</c> from its own directory, the way the Discord plugin already did.
/// </para>
/// </remarks>
public sealed class MicrosoftRuntimeTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly List<string> _roots = [];
    private string _executable = string.Empty;

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception leftBehind) when (leftBehind is IOException or UnauthorizedAccessException)
            {
                // Best effort. One directory left behind is not worth failing a test over.
            }
        }
    }

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

    /// <summary>Copies the real plugin somewhere disposable and points it at the stand-in.</summary>
    private string PluginRoot(string? apiBase)
    {
        var root = TestTemp.Folder("microsoft");
        var directory = Path.Combine(root, "plugin-microsoft");
        Directory.CreateDirectory(Path.Combine(directory, "work"));

        var source = Path.Combine(Repository().FullName, "plugins", "microsoft");

        foreach (var file in Directory.EnumerateFiles(source, "*.py"))
        {
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), overwrite: true);
        }

        _executable = Path.Combine(directory, "microsoft_service.py");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                _executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        if (apiBase is not null)
        {
            // The same seam the Discord plugin uses. Absent in every shipped installation, which
            // is what keeps the strict allowlist in force everywhere but here.
            File.WriteAllText(
                Path.Combine(directory, "config.json"),
                JsonSerializer.Serialize(new { api_base = apiBase }));
        }

        _roots.Add(root);
        return root;
    }

    private PluginManifest Manifest()
    {
        var json = File.ReadAllText(
            Path.Combine(Repository().FullName, "plugins", "microsoft", "plugin.json"));

        PluginManifestRead read = PluginManifestReader.Read(json, []);
        Assert.True(read.Ok, string.Join("; ", read.Problems));

        return read.Manifest! with
        {
            Executable = _executable,
            Service = read.Manifest!.Service! with { Executable = _executable },
            NetworkEndpoints = ["127.0.0.1"],
        };
    }

    private sealed class Secrets(IReadOnlyDictionary<string, string>? values) : IPluginSecretSource
    {
        public Task<string?> FindAsync(string pluginId, string name, CancellationToken ct) =>
            Task.FromResult(values is not null && values.TryGetValue(name, out var value)
                ? value
                : null);
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

    private ServicePluginHost Host(string root, IReadOnlyDictionary<string, string>? secrets) =>
        new(root,
            new UnconfinedSandbox("confinement has its own tests"),
            new Secrets(secrets),
            new Observations(),
            new TestClock(DateTimeOffset.UnixEpoch),
            allowUnconfined: true);

    private static readonly Dictionary<string, string> Configured = new()
    {
        ["tenant_id"] = "tenant-1",
        ["client_id"] = "client-1",
        ["refresh_token"] = "a-refresh-token-that-is-never-logged",
    };

    private static PluginInvocation Call(string capability, object input) =>
        new("plugin/microsoft", capability, JsonSerializer.Serialize(input),
            Sensitivity.Private, null, NetworkGranted: true);

    // ---- the runtime path, which had never run ----

    [Fact]
    public async Task AuroraCanStartThePluginAndGetAnAnswerBack()
    {
        var root = PluginRoot(apiBase: null);
        await using ServicePluginHost host = Host(root, Configured);

        PluginResult result = await host.InvokeAsync(Manifest(), Call("microsoft.status", new { }), Ct);

        Assert.True(result.Ok, $"{result.Refusal}: {result.Detail}");

        JsonNode output = JsonNode.Parse(result.OutputJson!)!;

        // The whole round trip: the host started the real program, delivered the secrets over its
        // pipe, sent a call frame, and read a result frame back.
        Assert.True(output["configured"]!.GetValue<bool>());
        Assert.Equal("refresh_token", output["grant"]!.GetValue<string>());
    }

    [Fact]
    public async Task AuroraRefusesToStartItWithoutTheSecretsItDeclared()
    {
        var root = PluginRoot(apiBase: null);
        await using ServicePluginHost host = Host(root, secrets: null);

        PluginResult result = await host.InvokeAsync(Manifest(), Call("microsoft.status", new { }), Ct);

        // The audit expected a degraded start here — the plugin has a branch for it, and reports
        // which secrets are missing. Aurora does not allow it: a service plugin whose declared
        // required secrets are absent is never started, and the refusal names the one that is
        // missing.
        //
        // Aurora is right. A plugin that cannot do its job should not be holding a process, and
        // "start it anyway and let it explain" is how something ends up running with half its
        // configuration. The plugin's degraded branch is reachable only when it is run standalone,
        // which is where its own tests run it, and that is recorded rather than treated as a
        // feature of the integration.
        Assert.False(result.Ok);
        Assert.Equal(PluginRefusal.ServiceUnavailable, result.Refusal);
        Assert.Contains("tenant_id", result.Detail ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACapabilityThatReachesGraphRoundTripsThroughTheHost()
    {
        using var graph = new FakeHttpService();

        graph.Answer(200, """{"access_token":"a-token","expires_in":3600}""");

        var root = PluginRoot(apiBase: $"http://127.0.0.1:{graph.Port}");
        await using ServicePluginHost host = Host(root, Configured);

        PluginResult result = await host.InvokeAsync(
            Manifest(), Call("microsoft.identity.me", new { }), Ct);

        Assert.True(result.Ok, $"{result.Refusal}: {result.Detail}");

        // Not just that the plugin answered — that it went out to a provider and came back, all
        // of it started and driven by Aurora's own host.
        Assert.NotEmpty(graph.Seen);
        Assert.Contains(graph.Seen, r => r.Path.Contains("/v1.0/me", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AProviderRefusalCrossesTheHostAsARefusalRatherThanACrash()
    {
        using var graph = new FakeHttpService();

        graph.Answer(200, """{"access_token":"a-token","expires_in":3600}""");
        graph.Answer(403, """{"error":{"code":"accessDenied","message":"no"}}""");

        var root = PluginRoot(apiBase: $"http://127.0.0.1:{graph.Port}");
        await using ServicePluginHost host = Host(root, Configured);

        PluginResult result = await host.InvokeAsync(
            Manifest(), Call("microsoft.identity.me", new { }), Ct);

        // A denial from Microsoft is an ordinary outcome. It has to arrive as one rather than as
        // the plugin dying, which would have Aurora restarting it on every permissions problem.
        Assert.False(result.Ok);
        Assert.Equal("microsoft_denied", result.Refusal);
    }

    [Fact]
    public async Task AnUnknownCapabilityIsRefusedWithoutStoppingThePlugin()
    {
        var root = PluginRoot(apiBase: null);
        await using ServicePluginHost host = Host(root, Configured);

        PluginResult refused = await host.InvokeAsync(
            Manifest(), Call("microsoft.mail.send_everything", new { }), Ct);

        Assert.False(refused.Ok);

        // And it is still there afterwards. A service plugin that died on a bad request would be
        // restarted by Aurora on every one.
        PluginResult after = await host.InvokeAsync(
            Manifest(), Call("microsoft.status", new { }), Ct);

        Assert.True(after.Ok, $"{after.Refusal}: {after.Detail}");
    }

    [Fact]
    public async Task NoCredentialCrossesTheHostInAResult()
    {
        using var graph = new FakeHttpService();

        graph.Answer(400, """
            {"error":"invalid_grant",
             "error_description":"AADSTS70008: The refresh token a-refresh-token-that-is-never-logged has expired."}
            """);

        var root = PluginRoot(apiBase: $"http://127.0.0.1:{graph.Port}");
        await using ServicePluginHost host = Host(root, Configured);

        PluginResult result = await host.InvokeAsync(
            Manifest(), Call("microsoft.identity.me", new { }), Ct);

        Assert.False(result.Ok);

        // Microsoft quotes the credential it rejected. Checked here as well as inside the plugin,
        // because this is the boundary where it would reach Aurora's audit.
        var whole = JsonSerializer.Serialize(result);

        Assert.DoesNotContain("a-refresh-token-that-is-never-logged", whole, StringComparison.Ordinal);
        Assert.Contains("AADSTS70008", result.Detail ?? "", StringComparison.Ordinal);
    }
}
