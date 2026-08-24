using Aurora.Adapters.Tools;
using Aurora.Adapters.Validation;
using Aurora.Adapters.Vault;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>Conformance tests for RFC 06.</summary>
public sealed class ToolManagerTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly Principal Caller = new("c1", "u1");

    /// <summary>A connector whose behaviour each test dictates.</summary>
    private sealed class StubConnector : IToolConnector
    {
        private readonly ToolManifest _manifest;
        private readonly Func<ToolCall, EphemeralSecretHandle?, ToolResult> _execute;
        private readonly Func<string, ToolResult>? _reconcile;

        public StubConnector(
            ToolManifest manifest,
            Func<ToolCall, EphemeralSecretHandle?, ToolResult>? execute = null,
            Func<string, ToolResult>? reconcile = null)
        {
            _manifest = manifest;
            _execute = execute ?? ((_, _) => Ok());
            _reconcile = reconcile;
        }

        public int Executions { get; private set; }

        public string? SecretSeen { get; private set; }

        public ToolManifest Describe() => _manifest;

        public Task<ToolResult> ExecuteAsync(
            ToolCall call, EphemeralSecretHandle? secret, CancellationToken ct)
        {
            Executions++;
            SecretSeen = secret?.Use(s => new string(s));
            return Task.FromResult(_execute(call, secret));
        }

        public Task<ToolResult> CancelAsync(string callId, CancellationToken ct) =>
            Task.FromResult(new ToolResult(
                ToolCallStatus.Cancelled, null, [], [], false, "cancelled"));

        public Task<ToolResult> ReconcileAsync(string callId, string? externalReference, CancellationToken ct) =>
            Task.FromResult(_reconcile?.Invoke(callId)
                ?? new ToolResult(ToolCallStatus.Succeeded, """{"ok":true}""", [], [], false, "found"));

        public static ToolResult Ok(string json = """{"ok":true}""") =>
            new(ToolCallStatus.Succeeded, json, [], [], false, "done");
    }

    private static ToolManifest Manifest(
        string toolId = "mailer",
        IReadOnlyList<string>? effects = null,
        int timeoutSeconds = 30,
        int rateLimit = 0,
        bool requiresApproval = false,
        string authMode = AuthMode.None,
        string? secretRef = null) =>
        new(toolId, "1.0", "smtp", ["communication.send"],
            InputSchema: """{"type":"object","required":["body"],"properties":{"body":{"type":"string"}}}""",
            OutputSchema: """{"type":"object","required":["ok"],"properties":{"ok":{"type":"boolean"}}}""",
            Effects: effects ?? ["message.send"],
            DataClassesIn: ["PRIVATE"], DataClassesOut: ["PRIVATE"],
            AuthMode: authMode, TimeoutSeconds: timeoutSeconds, RateLimitPerMinute: rateLimit,
            RequiresApproval: requiresApproval, SecretReferenceId: secretRef);

    private static (SqliteToolManager Manager, SqliteVault Vault) New(
        SqliteTestDb db, DateTimeOffset? now = null)
    {
        var clock = new TestClock(now ?? DateTimeOffset.UnixEpoch);
        var vault = new SqliteVault(
            db.Factory, new AesGcmSecretProtector(Enumerable.Repeat((byte)3, 32).ToArray()),
            clock, new RecordingAuditStore(), new FakePrincipalAccessor(Caller), VaultOptions.Default);

        return (new SqliteToolManager(db.Factory, new JsonSchemaValidator(), vault, clock), vault);
    }

    private static async Task<ToolCall> AuthorizedAsync(
        SqliteToolManager manager, string toolId = "mailer", string? idempotencyKey = "k1",
        string? approvalId = null)
    {
        ToolCall call = await manager.ProposeAsync(
            "work/1", null, toolId, "communication.send", """{"body":"hello"}""", idempotencyKey, Ct);

        return await manager.AuthorizeAsync(call.Id, ["policy/1"], approvalId, Ct);
    }

    // ---- rule 1: capabilities are explicit ----

    [Fact]
    public async Task AToolDeclaringNoCapabilityIsRefused()
    {
        using var db = new SqliteTestDb();
        var (manager, _) = New(db);

        await Assert.ThrowsAsync<ToolException>(() => manager.RegisterAsync(
            new StubConnector(Manifest() with { Capabilities = [] }), Ct));
    }

    [Fact]
    public async Task ACapabilityTheManifestDoesNotOfferIsRefused()
    {
        using var db = new SqliteTestDb();
        var (manager, _) = New(db);
        await manager.RegisterAsync(new StubConnector(Manifest()), Ct);

        await Assert.ThrowsAsync<ToolException>(() => manager.ProposeAsync(
            "work/1", null, "mailer", "files.delete", """{"body":"x"}""", "k1", Ct));
    }

    // ---- rule 2: a writing tool needs an idempotency key ----

    [Fact]
    public async Task AWritingToolWithoutAnIdempotencyKeyIsRefused()
    {
        using var db = new SqliteTestDb();
        var (manager, _) = New(db);
        await manager.RegisterAsync(new StubConnector(Manifest()), Ct);

        await Assert.ThrowsAsync<ToolException>(() => manager.ProposeAsync(
            "work/1", null, "mailer", "communication.send", """{"body":"x"}""", null, Ct));
    }

    [Fact]
    public async Task AReadOnlyToolNeedsNoIdempotencyKey()
    {
        using var db = new SqliteTestDb();
        var (manager, _) = New(db);
        await manager.RegisterAsync(new StubConnector(Manifest("reader", effects: [])), Ct);

        ToolCall call = await manager.ProposeAsync(
            "work/1", null, "reader", "communication.send", """{"body":"x"}""", null, Ct);

        Assert.Equal(ToolCallStatus.Proposed, call.Status);
    }

    // ---- input is validated before anything is attempted ----

    [Fact]
    public async Task AnInputFailingTheSchemaIsRefused()
    {
        using var db = new SqliteTestDb();
        var (manager, _) = New(db);
        await manager.RegisterAsync(new StubConnector(Manifest()), Ct);

        await Assert.ThrowsAsync<ToolException>(() => manager.ProposeAsync(
            "work/1", null, "mailer", "communication.send", """{"wrong":"field"}""", "k1", Ct));
    }

    [Fact]
    public async Task TheStoredInputIsRedacted()
    {
        using var db = new SqliteTestDb();
        var (manager, _) = New(db);
        await manager.RegisterAsync(new StubConnector(Manifest()), Ct);

        ToolCall call = await manager.ProposeAsync(
            "work/1", null, "mailer", "communication.send", """{"body":"my secret plans"}""", "k1", Ct);

        Assert.DoesNotContain("secret plans", call.InputRedactedJson, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(call.InputHash));
    }

    // ---- authorization ----

    [Fact]
    public async Task ACallIsNotExecutedWithoutAuthorization()
    {
        using var db = new SqliteTestDb();
        var (manager, _) = New(db);
        await manager.RegisterAsync(new StubConnector(Manifest()), Ct);
        ToolCall call = await manager.ProposeAsync(
            "work/1", null, "mailer", "communication.send", """{"body":"x"}""", "k1", Ct);

        await Assert.ThrowsAsync<ToolException>(() => manager.ExecuteAsync(call.Id, Ct));
    }

    [Fact]
    public async Task AuthorizationNeedsAPolicyDecision()
    {
        using var db = new SqliteTestDb();
        var (manager, _) = New(db);
        await manager.RegisterAsync(new StubConnector(Manifest()), Ct);
        ToolCall call = await manager.ProposeAsync(
            "work/1", null, "mailer", "communication.send", """{"body":"x"}""", "k1", Ct);

        await Assert.ThrowsAsync<ToolException>(() => manager.AuthorizeAsync(call.Id, [], null, Ct));
    }

    [Fact]
    public async Task AToolRequiringApprovalIsNotAuthorizedWithoutOne()
    {
        using var db = new SqliteTestDb();
        var (manager, _) = New(db);
        await manager.RegisterAsync(new StubConnector(Manifest(requiresApproval: true)), Ct);
        ToolCall call = await manager.ProposeAsync(
            "work/1", null, "mailer", "communication.send", """{"body":"x"}""", "k1", Ct);

        await Assert.ThrowsAsync<ToolException>(() => manager.AuthorizeAsync(call.Id, ["policy/1"], null, Ct));
    }

    // ---- rule 3: the external result is untrusted ----

    [Fact]
    public async Task AnOutputFailingTheSchemaFailsTheCall()
    {
        using var db = new SqliteTestDb();
        var (manager, _) = New(db);
        await manager.RegisterAsync(new StubConnector(
            Manifest(), (_, _) => StubConnector.Ok("""{"unexpected":"shape"}""")), Ct);

        ToolCall authorized = await AuthorizedAsync(manager);
        ToolCall executed = await manager.ExecuteAsync(authorized.Id, Ct);

        // A remote side that changed shape is a defect to surface, not a payload to read loosely.
        Assert.Equal(ToolCallStatus.Failed, executed.Status);
        Assert.Equal("output_schema_invalid", executed.ErrorCode);
    }

    [Fact]
    public async Task AnOutputThatIsNotJsonFailsTheCall()
    {
        using var db = new SqliteTestDb();
        var (manager, _) = New(db);
        await manager.RegisterAsync(new StubConnector(
            Manifest(), (_, _) => StubConnector.Ok("not json at all")), Ct);

        ToolCall executed = await manager.ExecuteAsync((await AuthorizedAsync(manager)).Id, Ct);

        Assert.Equal("output_not_json", executed.ErrorCode);
    }

    [Fact]
    public async Task AnOversizedOutputFailsTheCall()
    {
        using var db = new SqliteTestDb();
        var (manager, _) = New(db);
        var huge = $$"""{"ok":true,"padding":"{{new string('x', SqliteToolManager.MaxOutputBytes + 10)}}"}""";
        await manager.RegisterAsync(new StubConnector(Manifest(), (_, _) => StubConnector.Ok(huge)), Ct);

        ToolCall executed = await manager.ExecuteAsync((await AuthorizedAsync(manager)).Id, Ct);

        Assert.Equal("output_too_large", executed.ErrorCode);
    }

    [Fact]
    public async Task AValidOutputSucceeds()
    {
        using var db = new SqliteTestDb();
        var (manager, _) = New(db);
        await manager.RegisterAsync(new StubConnector(Manifest()), Ct);

        ToolCall executed = await manager.ExecuteAsync((await AuthorizedAsync(manager)).Id, Ct);

        Assert.Equal(ToolCallStatus.Succeeded, executed.Status);
        Assert.Null(executed.ErrorCode);
    }

    // ---- the UNKNOWN case ----

    [Fact]
    public async Task ATimeoutAfterDispatchBecomesUnknownAndIsNeverResent()
    {
        using var db = new SqliteTestDb();
        var (manager, _) = New(db);
        var connector = new StubConnector(
            Manifest(timeoutSeconds: 0),
            (_, _) => throw new OperationCanceledException());
        await manager.RegisterAsync(connector, Ct);

        ToolCall executed = await manager.ExecuteAsync((await AuthorizedAsync(manager)).Id, Ct);

        // "We did not receive a response" is not "it did not happen".
        Assert.Equal(ToolCallStatus.Unknown, executed.Status);
        Assert.Equal("timeout_after_dispatch", executed.ErrorCode);

        // And it is not simply run again.
        await Assert.ThrowsAsync<ToolException>(() => manager.ExecuteAsync(executed.Id, Ct));
        Assert.Single(await manager.UnknownCallsAsync(Ct));
    }

    [Fact]
    public async Task ReconcilingAnUnknownCallAsksTheRemoteSide()
    {
        using var db = new SqliteTestDb();
        var (manager, _) = New(db);
        var connector = new StubConnector(
            Manifest(timeoutSeconds: 0),
            (_, _) => throw new OperationCanceledException(),
            _ => new ToolResult(
                ToolCallStatus.Succeeded, """{"ok":true}""", [], [], false, "it had gone through",
                ExternalReference: "remote/42"));
        await manager.RegisterAsync(connector, Ct);
        ToolCall unknown = await manager.ExecuteAsync((await AuthorizedAsync(manager)).Id, Ct);

        ToolCall reconciled = await manager.ReconcileAsync(unknown.Id, Ct);

        Assert.Equal(ToolCallStatus.Succeeded, reconciled.Status);
        Assert.Equal("remote/42", reconciled.ExternalReference);

        // One dispatch, one reconcile. The effect was never attempted twice.
        Assert.Equal(1, connector.Executions);
    }

    [Fact]
    public async Task OnlyAnUnknownCallIsReconciled()
    {
        using var db = new SqliteTestDb();
        var (manager, _) = New(db);
        await manager.RegisterAsync(new StubConnector(Manifest()), Ct);
        ToolCall executed = await manager.ExecuteAsync((await AuthorizedAsync(manager)).Id, Ct);

        await Assert.ThrowsAsync<ToolException>(() => manager.ReconcileAsync(executed.Id, Ct));
    }

    // ---- rate limit ----

    [Fact]
    public async Task ARateLimitedToolIsQueuedWithARetryTime()
    {
        using var db = new SqliteTestDb();
        var (manager, _) = New(db);
        await manager.RegisterAsync(new StubConnector(Manifest(rateLimit: 1)), Ct);

        await manager.ExecuteAsync((await AuthorizedAsync(manager)).Id, Ct);
        ToolCall second = await manager.ExecuteAsync((await AuthorizedAsync(manager)).Id, Ct);

        // Deferred with a time on it, rather than a tight repetition.
        Assert.Equal(ToolCallStatus.Queued, second.Status);
        Assert.NotNull(second.RetryAfterUtc);
    }

    // ---- rule 5: a connector gets its own secret, never another's ----

    [Fact]
    public async Task AConnectorReceivesAHandleForItsOwnSecret()
    {
        using var db = new SqliteTestDb();
        var (manager, vault) = New(db);
        SecretReference secret = await vault.PutAsync("smtp", ["mailer"], "hunter2-value", null, Ct);
        var connector = new StubConnector(
            Manifest(authMode: AuthMode.VaultSecret, secretRef: secret.Id));
        await manager.RegisterAsync(connector, Ct);

        await manager.ExecuteAsync((await AuthorizedAsync(manager)).Id, Ct);

        Assert.Equal("hunter2-value", connector.SecretSeen);
    }

    [Fact]
    public async Task AConnectorCannotUseAnotherConnectorsSecret()
    {
        using var db = new SqliteTestDb();
        var (manager, vault) = New(db);

        // The secret is allowed to "mailer" only; "browser" asks for it by id.
        SecretReference secret = await vault.PutAsync("smtp", ["mailer"], "hunter2-value", null, Ct);
        await manager.RegisterAsync(new StubConnector(
            Manifest("browser", authMode: AuthMode.VaultSecret, secretRef: secret.Id)), Ct);

        ToolCall authorized = await AuthorizedAsync(manager, "browser");

        await Assert.ThrowsAsync<VaultException>(() => manager.ExecuteAsync(authorized.Id, Ct));
    }

    // ---- a changed remote schema disables the tool ----

    [Fact]
    public async Task ADisabledToolAcceptsNoFurtherCalls()
    {
        using var db = new SqliteTestDb();
        var (manager, _) = New(db);
        await manager.RegisterAsync(new StubConnector(Manifest()), Ct);

        Assert.Equal(1, await manager.DisableAsync("mailer", "remote schema changed", Ct));

        ToolException error = await Assert.ThrowsAsync<ToolException>(() => manager.ProposeAsync(
            "work/1", null, "mailer", "communication.send", """{"body":"x"}""", "k1", Ct));

        Assert.Contains("remote schema changed", error.Message, StringComparison.Ordinal);
    }
}
