using System.Text.Json;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Kernel;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

public sealed class KernelTests
{
    private const string EchoSchema =
        """{"type":"object","additionalProperties":false,"required":["message"],"properties":{"message":{"type":"string"}}}""";

    private static readonly Principal Caller = new("client-1", "user-1");

    private static FakeCapability EchoCapability()
    {
        var descriptor = FakeCapability.LowReadOnly("echo.say", EchoSchema);
        return new FakeCapability(descriptor, input =>
        {
            var message = input.TryGetProperty("message", out var m) ? m.GetString() ?? string.Empty : string.Empty;
            return JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["said"] = message });
        });
    }

    private static AuroraKernel Build(
        FakeCapability capability,
        bool valid = true,
        bool allow = true,
        bool grant = true,
        ReasonerProposal? proposal = null,
        IIdempotencyStore? idempotency = null,
        RecordingAuditStore? audit = null) =>
        new(
            new FakeReasoner(proposal),
            new FakeRegistry(capability),
            new FakeValidator(valid),
            new FakePolicy(allow),
            new FakeConsent(grant),
            new DirectExecutor(),
            audit ?? new RecordingAuditStore(),
            idempotency ?? new InMemoryIdempotencyStore());

    private static JsonElement Message(string text) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["message"] = text });

    [Fact]
    public async Task BothModes_IsInvalid()
    {
        var kernel = Build(EchoCapability());
        var response = await kernel.ExecuteAsync(
            new ExecuteRequest(Objective: "hi", ActionId: "echo.say"), Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Invalid, response.Status);
        Assert.Equal(ErrorCodes.BothModes, response.Error?.Code);
    }

    [Fact]
    public async Task NoMode_IsInvalid()
    {
        var kernel = Build(EchoCapability());
        var response = await kernel.ExecuteAsync(new ExecuteRequest(), Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Invalid, response.Status);
        Assert.Equal(ErrorCodes.NoMode, response.Error?.Code);
    }

    [Fact]
    public async Task UnknownAction_IsInvalid()
    {
        var kernel = Build(EchoCapability());
        var response = await kernel.ExecuteAsync(
            new ExecuteRequest(ActionId: "does.not.exist", Input: Message("x")), Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Invalid, response.Status);
        Assert.Equal(ErrorCodes.UnknownAction, response.Error?.Code);
    }

    [Fact]
    public async Task Objective_WithNoReasoner_IsUnavailable()
    {
        var kernel = Build(EchoCapability(), proposal: null);
        var response = await kernel.ExecuteAsync(
            new ExecuteRequest(Objective: "please echo"), Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Invalid, response.Status);
        Assert.Equal(ErrorCodes.ObjectiveUnavailable, response.Error?.Code);
    }

    [Fact]
    public async Task SchemaInvalid_IsRejected()
    {
        var kernel = Build(EchoCapability(), valid: false);
        var response = await kernel.ExecuteAsync(
            new ExecuteRequest(ActionId: "echo.say", Input: Message("x")), Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Invalid, response.Status);
        Assert.Equal(ErrorCodes.SchemaInvalid, response.Error?.Code);
    }

    [Fact]
    public async Task PolicyDenied_IsDeniedAndAudited()
    {
        var audit = new RecordingAuditStore();
        var kernel = Build(EchoCapability(), allow: false, audit: audit);
        var response = await kernel.ExecuteAsync(
            new ExecuteRequest(ActionId: "echo.say", Input: Message("x")), Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Denied, response.Status);
        Assert.Equal(ErrorCodes.PolicyDenied, response.Error?.Code);
        Assert.Equal(["policy_denied"], audit.Outcomes);
    }

    [Fact]
    public async Task ConsentRefused_IsDenied()
    {
        var kernel = Build(EchoCapability(), grant: false);
        var response = await kernel.ExecuteAsync(
            new ExecuteRequest(ActionId: "echo.say", Input: Message("x")), Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Denied, response.Status);
        Assert.Equal(ErrorCodes.ConsentRequired, response.Error?.Code);
    }

    [Fact]
    public async Task Happy_Completes_WithResultAndAudit()
    {
        var audit = new RecordingAuditStore();
        var capability = EchoCapability();
        var kernel = Build(capability, audit: audit);

        var response = await kernel.ExecuteAsync(
            new ExecuteRequest(ActionId: "echo.say", Input: Message("hello")), Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Completed, response.Status);
        Assert.Equal("explicit", response.Resolved?.Via);
        Assert.NotNull(response.Result);
        Assert.Equal("hello", response.Result!.Value.GetProperty("said").GetString());
        Assert.Equal(["completed"], audit.Outcomes);
        Assert.Equal(["audit-1"], response.AuditRef);
        Assert.Equal(1, capability.ExecuteCount);
    }

    [Fact]
    public async Task Idempotent_Replay_ReturnsStored_WithoutReExecuting()
    {
        var store = new InMemoryIdempotencyStore();
        var capability = EchoCapability();
        var kernel = Build(capability, idempotency: store);
        var request = new ExecuteRequest(ActionId: "echo.say", Input: Message("hi"), IdempotencyKey: "k1");

        var first = await kernel.ExecuteAsync(request, Caller, CancellationToken.None);
        var second = await kernel.ExecuteAsync(request, Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Completed, first.Status);
        Assert.Equal(ExecuteStatus.Completed, second.Status);
        Assert.Equal("hi", second.Result!.Value.GetProperty("said").GetString());
        Assert.Equal(1, capability.ExecuteCount); // second call served from the idempotency store
    }

    [Fact]
    public async Task Idempotent_Conflict_OnDifferentInput()
    {
        var store = new InMemoryIdempotencyStore();
        var kernel = Build(EchoCapability(), idempotency: store);

        await kernel.ExecuteAsync(
            new ExecuteRequest(ActionId: "echo.say", Input: Message("one"), IdempotencyKey: "k1"),
            Caller, CancellationToken.None);
        var conflict = await kernel.ExecuteAsync(
            new ExecuteRequest(ActionId: "echo.say", Input: Message("two"), IdempotencyKey: "k1"),
            Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Conflict, conflict.Status);
        Assert.Equal(ErrorCodes.IdempotencyConflict, conflict.Error?.Code);
    }

    [Fact]
    public async Task InputTooLarge_IsInvalid()
    {
        var kernel = Build(EchoCapability());
        var big = new string('a', AuroraLimits.MaxInputBytes + 10);
        var response = await kernel.ExecuteAsync(
            new ExecuteRequest(ActionId: "echo.say", Input: Message(big)), Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Invalid, response.Status);
        Assert.Equal(ErrorCodes.InputTooLarge, response.Error?.Code);
    }

    [Fact]
    public async Task Cancellation_DuringExecute_SettlesIndeterminate_AndReplayConflicts()
    {
        var store = new InMemoryIdempotencyStore();
        var cancelling = new FakeCapability(
            FakeCapability.LowReadOnly("boom.op", EchoSchema),
            _ => throw new OperationCanceledException());
        var kernel = Build(cancelling, idempotency: store);
        var request = new ExecuteRequest(ActionId: "boom.op", Input: Message("x"), IdempotencyKey: "kc");

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => kernel.ExecuteAsync(request, Caller, CancellationToken.None));

        // The reservation is settled to an indeterminate state, so a replay is a deterministic
        // conflict rather than an eternal in-progress.
        var replay = await kernel.ExecuteAsync(request, Caller, CancellationToken.None);
        Assert.Equal(ExecuteStatus.Conflict, replay.Status);
        Assert.Equal(ErrorCodes.UnknownState, replay.Error?.Code);
    }
}
