using Aurora.Adapters.Tools;
using Aurora.Adapters.Validation;
using Aurora.Adapters.Vault;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Tests.Support;

/// <summary>Shared scaffolding for tests that need a tool manager and a connector.</summary>
public static class ToolManagerTestsSupport
{
    private static readonly Principal Caller = new("c1", "u1");

    public static SqliteToolManager Manager(SqliteTestDb db, out SqliteVault vault)
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        vault = new SqliteVault(
            db.Factory, new AesGcmSecretProtector(Enumerable.Repeat((byte)3, 32).ToArray()),
            clock, new RecordingAuditStore(), new FakePrincipalAccessor(Caller), VaultOptions.Default);

        return new SqliteToolManager(db.Factory, new JsonSchemaValidator(), vault, clock);
    }

    public static ToolManifest Manifest(
        string toolId = "mailer", int timeoutSeconds = 30, bool requiresApproval = false) => new(
        toolId, "1.0", "smtp", ["communication.send"],
        InputSchema: """{"type":"object","required":["body"],"properties":{"body":{"type":"string"}}}""",
        OutputSchema: """{"type":"object","required":["ok"],"properties":{"ok":{"type":"boolean"}}}""",
        Effects: ["message.send"], DataClassesIn: ["PRIVATE"], DataClassesOut: ["PRIVATE"],
        AuthMode: AuthMode.None, TimeoutSeconds: timeoutSeconds, RateLimitPerMinute: 0,
        RequiresApproval: requiresApproval);

    public static IToolConnector Connector(bool requiresApproval = false) =>
        new SimpleConnector(Manifest(requiresApproval: requiresApproval));

    /// <summary>A connector that never answers, so the manager times out after dispatch.</summary>
    public static IToolConnector TimingOutConnector() =>
        new SimpleConnector(Manifest(timeoutSeconds: 0), timesOut: true);

    private sealed class SimpleConnector(ToolManifest manifest, bool timesOut = false) : IToolConnector
    {
        public ToolManifest Describe() => manifest;

        public Task<ToolResult> ExecuteAsync(
            ToolCall call, EphemeralSecretHandle? secret, CancellationToken ct) =>
            timesOut
                ? throw new OperationCanceledException()
                : Task.FromResult(new ToolResult(
                    ToolCallStatus.Succeeded, """{"ok":true}""", [], [], false, "done"));

        public Task<ToolResult> CancelAsync(string callId, CancellationToken ct) =>
            Task.FromResult(new ToolResult(ToolCallStatus.Cancelled, null, [], [], false, "cancelled"));

        public Task<ToolResult> ReconcileAsync(string callId, string? externalReference, CancellationToken ct) =>
            Task.FromResult(new ToolResult(
                ToolCallStatus.Succeeded, """{"ok":true}""", [], [], false, "found"));
    }
}
