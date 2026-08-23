using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora.Adapters.Capabilities;
using Aurora.Adapters.Consent;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Policy;
using Aurora.Adapters.Validation;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

public sealed class AdapterTests
{
    private static readonly Principal Caller = new("c1", "u1");
    private static readonly ISchemaValidator Validator = new JsonSchemaValidator();
    private static readonly EchoSayCapability Echo = new();

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static CapabilityDescriptor Descriptor(RiskLevel risk, params string[] effects) =>
        new("x", "x", "x", Parse("{}"), effects, risk, false);

    private static CapabilityDescriptor ApprovalGatedDescriptor(RiskLevel risk) =>
        new("x", "x", "x", Parse("{}"), ["writes"], risk, ApprovalRequired: true);

    // --- JSON Schema validation (real JsonSchema.Net) ---

    [Fact]
    public void Validator_AcceptsValidInput() =>
        Assert.True(Validator.Validate(Echo.Descriptor.InputSchema, Parse("""{"message":"hi"}""")).IsValid);

    [Fact]
    public void Validator_RejectsMissingRequired() =>
        Assert.False(Validator.Validate(Echo.Descriptor.InputSchema, Parse("{}")).IsValid);

    [Fact]
    public void Validator_RejectsUnknownField() =>
        Assert.False(Validator.Validate(Echo.Descriptor.InputSchema, Parse("""{"message":"hi","extra":1}""")).IsValid);

    [Fact]
    public void Validator_RejectsWrongType() =>
        Assert.False(Validator.Validate(Echo.Descriptor.InputSchema, Parse("""{"message":5}""")).IsValid);

    [Fact]
    public void Validator_RejectsOversizedString()
    {
        var input = JsonSerializer.SerializeToElement(new JsonObject { ["message"] = new string('a', 2001) });
        Assert.False(Validator.Validate(Echo.Descriptor.InputSchema, input).IsValid);
    }

    // --- Policy (fail-closed) ---

    [Fact]
    public void Policy_AllowsLowReadOnly() =>
        Assert.True(new AllowlistPolicyEngine().Evaluate(Descriptor(RiskLevel.Low), Parse("{}"), Caller).Allowed);

    [Fact]
    public void Policy_DeniesMedium() =>
        Assert.False(new AllowlistPolicyEngine().Evaluate(Descriptor(RiskLevel.Medium), Parse("{}"), Caller).Allowed);

    [Fact]
    public void Policy_DeniesLowWithEffects() =>
        Assert.False(new AllowlistPolicyEngine().Evaluate(Descriptor(RiskLevel.Low, "writes"), Parse("{}"), Caller).Allowed);

    [Fact]
    public void Policy_AllowsMediumWithApprovalRequired() =>
        Assert.True(new AllowlistPolicyEngine()
            .Evaluate(ApprovalGatedDescriptor(RiskLevel.Medium), Parse("{}"), Caller).Allowed);

    [Fact]
    public void Policy_DeniesHighEvenWithApprovalRequired() =>
        Assert.False(new AllowlistPolicyEngine()
            .Evaluate(ApprovalGatedDescriptor(RiskLevel.High), Parse("{}"), Caller).Allowed);

    // --- Consent gate (It.2, docs/adr/0002) ---

    [Fact]
    public async Task Consent_AutoGrantsLow()
    {
        var outcome = await new SessionAwareConsentGate(new FakeApprovalStore(), new NoConsentSessionStore())
            .EvaluateAsync(Descriptor(RiskLevel.Low), Parse("{}"), "scope-1", Caller, CancellationToken.None);
        Assert.True(outcome.Granted);
        Assert.Equal(ConsentDecision.AutoLow, outcome.Info.Decision);
    }

    [Fact]
    public async Task Consent_RefusesMediumWithoutApprovalRequired()
    {
        var outcome = await new SessionAwareConsentGate(new FakeApprovalStore(), new NoConsentSessionStore())
            .EvaluateAsync(Descriptor(RiskLevel.Medium), Parse("{}"), "scope-1", Caller, CancellationToken.None);
        Assert.False(outcome.Granted);
        Assert.Equal(ConsentDecision.Denied, outcome.Info.Decision);
    }

    [Fact]
    public async Task Consent_ApprovalGated_RequestsThenGrantsOnceApproved()
    {
        var approvals = new FakeApprovalStore();
        var gate = new SessionAwareConsentGate(approvals, new NoConsentSessionStore());
        var descriptor = ApprovalGatedDescriptor(RiskLevel.Medium);

        var first = await gate.EvaluateAsync(descriptor, Parse("{}"), "scope-1", Caller, CancellationToken.None);
        Assert.False(first.Granted);
        Assert.Equal(ConsentDecision.RequiresApproval, first.Info.Decision);
        var approvalId = first.Info.ApprovalId!;

        await approvals.DecideAsync(Caller, approvalId, approve: true, CancellationToken.None);

        var second = await gate.EvaluateAsync(descriptor, Parse("{}"), "scope-1", Caller, CancellationToken.None);
        Assert.True(second.Granted);
        Assert.Equal(ConsentDecision.Granted, second.Info.Decision);

        // One-time use: a third evaluation for the same scope has nothing live to consume.
        var third = await gate.EvaluateAsync(descriptor, Parse("{}"), "scope-1", Caller, CancellationToken.None);
        Assert.False(third.Granted);
        Assert.Equal(ConsentDecision.RequiresApproval, third.Info.Decision);
        Assert.NotEqual(approvalId, third.Info.ApprovalId);
    }

    [Fact]
    public async Task Consent_ApprovalGated_StaysDeniedAfterRejection()
    {
        var approvals = new FakeApprovalStore();
        var gate = new SessionAwareConsentGate(approvals, new NoConsentSessionStore());
        var descriptor = ApprovalGatedDescriptor(RiskLevel.Medium);

        var first = await gate.EvaluateAsync(descriptor, Parse("{}"), "scope-1", Caller, CancellationToken.None);
        await approvals.DecideAsync(Caller, first.Info.ApprovalId!, approve: false, CancellationToken.None);

        var second = await gate.EvaluateAsync(descriptor, Parse("{}"), "scope-1", Caller, CancellationToken.None);
        Assert.False(second.Granted);
        Assert.Equal(ConsentDecision.Denied, second.Info.Decision);
    }

    // --- memory.remember / memory.recall (docs/adr/0002) ---

    [Fact]
    public async Task RememberNote_ThenRecall_ReturnsIt()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var notes = new SqliteNoteStore(db.Factory, clock);
        var principals = new FakePrincipalAccessor(Caller);

        var remember = new RememberNoteCapability(notes, principals);
        var recall = new RecallNotesCapability(notes, principals);

        var saveResult = await remember.ExecuteAsync(Parse("""{"note":"buy milk"}"""), CancellationToken.None);
        Assert.Equal("buy milk", saveResult.GetProperty("note").GetString());

        var recallResult = await recall.ExecuteAsync(Parse("{}"), CancellationToken.None);
        var listed = recallResult.GetProperty("notes").EnumerateArray().ToList();
        Assert.Single(listed);
        Assert.Equal("buy milk", listed[0].GetProperty("note").GetString());
    }

    [Fact]
    public async Task RememberNote_IsScopedPerPrincipal()
    {
        using var db = new SqliteTestDb();
        var notes = new SqliteNoteStore(db.Factory, new TestClock(DateTimeOffset.UnixEpoch));

        await new RememberNoteCapability(notes, new FakePrincipalAccessor(Caller))
            .ExecuteAsync(Parse("""{"note":"mine"}"""), CancellationToken.None);

        var other = new Principal("c2", "u2");
        var recallResult = await new RecallNotesCapability(notes, new FakePrincipalAccessor(other))
            .ExecuteAsync(Parse("{}"), CancellationToken.None);

        Assert.Empty(recallResult.GetProperty("notes").EnumerateArray());
    }

    // --- Capabilities ---

    [Fact]
    public async Task ClockNow_ReturnsInjectedTime()
    {
        var clock = new TestClock(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
        var result = await new ClockNowCapability(clock).ExecuteAsync(Parse("{}"), CancellationToken.None);

        Assert.Equal(1_700_000_000_000, result.GetProperty("unix_ms").GetInt64());
        Assert.False(string.IsNullOrEmpty(result.GetProperty("utc").GetString()));
    }

    [Fact]
    public async Task EchoSay_EchoesMessage()
    {
        var result = await Echo.ExecuteAsync(Parse("""{"message":"hola"}"""), CancellationToken.None);
        Assert.Equal("hola", result.GetProperty("said").GetString());
    }

    // --- Registry ---

    [Fact]
    public void Registry_ListsFiltersAndLooksUp()
    {
        var registry = new StaticCapabilityRegistry(
            [new ClockNowCapability(new TestClock(DateTimeOffset.UnixEpoch)), new EchoSayCapability()]);

        Assert.Equal(2, registry.List(null).Count);
        Assert.Single(registry.List("clock"));
        Assert.True(registry.TryGet("echo.say", out _));
        Assert.False(registry.TryGet("nope", out _));
    }
}
