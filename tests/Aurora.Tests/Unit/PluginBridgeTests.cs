using System.Text.Json;
using Aurora.Adapters.Capabilities;
using Aurora.Adapters.Plugins;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// A plugin's capability, as an ordinary Aurora capability (docs/adr/0062).
/// </summary>
/// <remarks>
/// The point of the bridge is that there is nothing special about it: same catalogue, same policy,
/// same approval, same cycle, same audit log. What is asserted here is that it really does go
/// through the registry, because the registry is where the permission check, the classification
/// ceiling, the circuit breaker and the secret-shaped-output refusal live.
/// </remarks>
public sealed class PluginBridgeTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed class RecordingRegistry(PluginResult result) : IPluginRegistry
    {
        public List<PluginInvocation> Calls { get; } = [];

        public Task<PluginResult> InvokeAsync(PluginInvocation invocation, CancellationToken ct)
        {
            Calls.Add(invocation);
            return Task.FromResult(result);
        }

        public Task<PluginVerification> VerifyAsync(PluginManifest manifest, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PluginInstallation> InstallAsync(
            PluginManifest manifest, IReadOnlyList<string> grantedPermissions, string approvalRef,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<PluginInstallation> UpdateAsync(PluginManifest manifest, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PluginInstallation> DisableAsync(string installationId, string actor, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PluginInstallation> ReleaseAsync(
            string installationId, string actor, string reason, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> PermittedSubscriptionsAsync(string pluginId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PluginInstallation?> GetAsync(string pluginId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PluginInstallation>> ListAsync(CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static PluginManifest Manifest() => new(
        "acme/notes", "1.0.0", "acme", "sig", 1,
        [Capability()], [], ["notes.write"], Sensitivity.Private, [], "README.md", "hash",
        "/plugins/acme/run.py");

    private static PluginCapability Capability(
        RiskLevel risk = RiskLevel.Medium, bool reversible = false) => new(
        "notes.append", """{"type":"object"}""", "{}", ["notes.write"],
        ApprovalRequired: true, RateLimitPerMinute: 30, Timeout: TimeSpan.FromSeconds(5),
        IdempotencySupport: true, AuditLevel: "FULL",
        Title: "Append a note", Description: "Adds a line.", Risk: risk, Reversible: reversible);

    private static PluginCapabilityBridge Bridge(PluginResult result, out RecordingRegistry registry) =>
        Bridge(result, out registry, out _);

    private static PluginCapabilityBridge Bridge(
        PluginResult result, out RecordingRegistry registry, out RecordingSecurityWatch watch)
    {
        registry = new RecordingRegistry(result);
        watch = new RecordingSecurityWatch();
        return new PluginCapabilityBridge(registry, watch, Manifest(), Capability());
    }

    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void TheDescriptorSaysWhoWroteIt()
    {
        CapabilityDescriptor descriptor =
            new PluginCapabilityBridge(
                new RecordingRegistry(new PluginResult(true, "{}", null, "", 1)),
                new RecordingSecurityWatch(), Manifest(), Capability()).Descriptor;

        Assert.Equal("notes.append", descriptor.ActionId);
        Assert.Equal(RiskLevel.Medium, descriptor.Risk);
        Assert.True(descriptor.ApprovalRequired);

        // At an approval prompt, a plugin's capability that looked exactly like one of Aurora's
        // own would be the wrong thing to show somebody.
        Assert.Contains("plugin acme/notes by acme", descriptor.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItGoesThroughTheRegistryRatherThanStraightToTheProgram()
    {
        PluginCapabilityBridge bridge = Bridge(
            new PluginResult(true, """{"appended":true}""", null, "completed", 4), out RecordingRegistry registry);

        JsonElement result = await bridge.ExecuteAsync(Input("""{"line":"hello"}"""), Ct);

        Assert.True(result.GetProperty("appended").GetBoolean());

        PluginInvocation call = Assert.Single(registry.Calls);
        Assert.Equal("acme/notes", call.PluginId);
        Assert.Equal("notes.append", call.CapabilityKey);

        // The manifest's ceiling travels with the call: the registry refuses anything above what
        // the plugin declared it may ever be handed.
        Assert.Equal(Sensitivity.Private, call.DataClass);
    }

    [Fact]
    public async Task ARefusalFromTheRegistryIsAFailedCallAndSaysWhy()
    {
        PluginCapabilityBridge bridge = Bridge(
            new PluginResult(false, null, PluginRefusal.PermissionNotGranted, "never granted: notes.write", 1),
            out _);

        PluginException refused = await Assert.ThrowsAsync<PluginException>(
            () => bridge.ExecuteAsync(Input("{}"), Ct).AsTask());

        Assert.Contains(PluginRefusal.PermissionNotGranted, refused.Message, StringComparison.Ordinal);
        Assert.Contains("never granted", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SomethingThatIsNotJsonIsAFailedCallAndNotAStringResult()
    {
        PluginCapabilityBridge bridge = Bridge(
            new PluginResult(true, "I am not JSON", null, "completed", 1), out _);

        // RFC 06 rule 3: an external result is untrusted until it validates.
        PluginException refused = await Assert.ThrowsAsync<PluginException>(
            () => bridge.ExecuteAsync(Input("{}"), Ct).AsTask());

        Assert.Contains("did not return JSON", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AuroraSOwnCapabilityWinsACollision()
    {
        var built = new StaticCapabilityRegistry([new EchoSayCapability()]);

        var shadow = new PluginCapabilityBridge(
            new RecordingRegistry(new PluginResult(true, "{}", null, "", 1)),
            new RecordingSecurityWatch(),
            Manifest(),
            Capability() with { Key = "echo.say", Title = "Not the real one" });

        var composite = new CompositeCapabilityRegistry(
            built, new StaticCapabilityRegistry([shadow]));

        Assert.True(composite.TryGet("echo.say", out ICapability? resolved));
        Assert.IsType<EchoSayCapability>(resolved);

        // And it appears once, not twice, so nobody reading the catalogue has to work out which.
        Assert.Single(composite.List(null), d => d.ActionId == "echo.say");
    }

    [Theory]
    [InlineData(PluginRefusal.PermissionNotGranted)]
    [InlineData(PluginRefusal.UndeclaredEffect)]
    [InlineData(PluginRefusal.AboveDeclaredClassification)]
    public async Task ReachingPastTheManifestIsReportedAsAnEscalation(string refusal)
    {
        PluginCapabilityBridge bridge = Bridge(
            new PluginResult(false, null, refusal, "never granted: notes.write", 1),
            out _, out RecordingSecurityWatch watch);

        await Assert.ThrowsAsync<PluginException>(
            () => bridge.ExecuteAsync(Input("{}"), Ct).AsTask());

        // The manifest reader refuses an undeclared permission at install and the catalogue
        // refuses an unknown action, so a call arriving here with one of these got past both.
        (var actor, var resource, var detail) = Assert.Single(watch.Escalations);
        Assert.Equal("acme/notes", actor);
        Assert.Equal("plugin/acme/notes", resource);
        Assert.Contains(refusal, detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOrdinaryFailureIsNotAnEscalation()
    {
        PluginCapabilityBridge bridge = Bridge(
            new PluginResult(false, null, "timed_out", "took longer than 5s", 5000),
            out _, out RecordingSecurityWatch watch);

        await Assert.ThrowsAsync<PluginException>(
            () => bridge.ExecuteAsync(Input("{}"), Ct).AsTask());

        // A plugin that hung is a plugin that hung. Reporting it as an attempt to exceed its
        // authority would make the incident log useless for the thing it is for.
        Assert.Empty(watch.Escalations);
    }
}
