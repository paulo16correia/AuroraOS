using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora.Adapters.Capabilities;
using Aurora.Adapters.Consent;
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

    // --- Consent gate ---

    [Fact]
    public void Consent_AutoGrantsLow()
    {
        var outcome = new AutoLowConsentGate().Evaluate(Descriptor(RiskLevel.Low), Caller);
        Assert.True(outcome.Granted);
        Assert.Equal(ConsentDecision.AutoLow, outcome.Info.Decision);
    }

    [Fact]
    public void Consent_RefusesMedium()
    {
        var outcome = new AutoLowConsentGate().Evaluate(Descriptor(RiskLevel.Medium), Caller);
        Assert.False(outcome.Granted);
        Assert.Equal(ConsentDecision.RequiresApproval, outcome.Info.Decision);
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
