using System.Text;
using System.Text.Json;
using Aurora.Adapters.Reasoning;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// The keyword proposer, which is the only one there is (docs/adr/0051).
/// </summary>
/// <remarks>
/// It matches words against Aurora's own catalogue and declines rather than guessing. Language
/// understanding belongs to the LLM client; this exists so an objective still resolves to
/// something harmless when nobody is there to interpret it.
/// </remarks>
public sealed class ReasonerTests
{
    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static CapabilityDescriptor Low(string actionId, string schema) => new(
        ActionId: actionId,
        Title: actionId,
        Description: actionId,
        InputSchema: Schema(schema),
        Effects: Array.Empty<string>(),
        Risk: RiskLevel.Low,
        ApprovalRequired: false);

    private static readonly CapabilityDescriptor ClockNow =
        Low("clock.now", """{"type":"object","additionalProperties":false,"properties":{}}""");

    private static readonly CapabilityDescriptor EchoSay =
        Low("echo.say", """{"type":"object","required":["message"],"properties":{"message":{"type":"string"}}}""");

    private static readonly CapabilityDescriptor RememberNote = new(
        ActionId: "memory.remember",
        Title: "Remember",
        Description: "Remember a note",
        InputSchema: Schema("""{"type":"object","required":["note"],"properties":{"note":{"type":"string"}}}"""),
        Effects: ["memory.write"],
        Risk: RiskLevel.Medium,
        ApprovalRequired: true);

    private static readonly CapabilityDescriptor[] Catalog = [ClockNow, EchoSay, RememberNote];

    // ---- keyword fallback ----

    [Fact]
    public async Task Keyword_ProposesNoInputAction_WhenSchemaRequiresNothing()
    {
        var proposal = await new KeywordReasoner()
            .ProposeAsync("tell me what time it is now", Catalog, CancellationToken.None);

        Assert.NotNull(proposal);
        Assert.Equal("clock.now", proposal!.ActionId);
        Assert.Equal(ResolutionVia.Keyword, proposal.Via);
    }

    [Fact]
    public async Task Keyword_FillsSingleRequiredStringFromRemainingText()
    {
        var proposal = await new KeywordReasoner()
            .ProposeAsync("say hello world", Catalog, CancellationToken.None);

        Assert.NotNull(proposal);
        Assert.Equal("echo.say", proposal!.ActionId);
        Assert.Equal("hello world", proposal.Input!.Value.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Keyword_NeverProposesAnythingAboveLow()
    {
        // "remember" matches memory.remember, which is MEDIUM with a write effect.
        var proposal = await new KeywordReasoner()
            .ProposeAsync("remember buy milk", Catalog, CancellationToken.None);

        Assert.Null(proposal);
    }

    [Fact]
    public async Task Keyword_DeclinesWhenItWouldHaveToInventTheArgument()
    {
        // Matches "say" but leaves nothing to put in the required field.
        var proposal = await new KeywordReasoner().ProposeAsync("say", Catalog, CancellationToken.None);

        Assert.Null(proposal);
    }

    [Fact]
    public async Task Keyword_ReturnsNullWhenNothingMatches()
    {
        var proposal = await new KeywordReasoner()
            .ProposeAsync("book me a flight to Lisbon", Catalog, CancellationToken.None);

        Assert.Null(proposal);
    }}
