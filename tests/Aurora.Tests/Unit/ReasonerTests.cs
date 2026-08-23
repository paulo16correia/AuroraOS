using System.Net;
using System.Text;
using System.Text.Json;
using Aurora.Adapters.Reasoning;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Xunit;

namespace Aurora.Tests.Unit;

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
    }

    // ---- Azure OpenAI ----

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static string Completion(string content) =>
        JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content } } },
        });

    private static AzureOpenAiReasoner Reasoner(StubHandler handler) =>
        new(new HttpClient(handler), new AzureOpenAiOptions("https://x.openai.azure.com", "gpt", "secret"));

    [Fact]
    public async Task Azure_ParsesProposal()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            Completion("""{"action_id":"echo.say","input":{"message":"hi"},"confidence":0.9}"""));

        var proposal = await Reasoner(handler).ProposeAsync("greet the user", Catalog, CancellationToken.None);

        Assert.NotNull(proposal);
        Assert.Equal("echo.say", proposal!.ActionId);
        Assert.Equal("hi", proposal.Input!.Value.GetProperty("message").GetString());
        Assert.Equal(0.9, proposal.Confidence, 3);
        Assert.Equal(ResolutionVia.Reasoner, proposal.Via);
    }

    [Fact]
    public async Task Azure_SendsApiKeyHeaderAndDeploymentUrl()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Completion("""{"action_id":null}"""));

        await Reasoner(handler).ProposeAsync("anything", Catalog, CancellationToken.None);

        Assert.Contains("/openai/deployments/gpt/chat/completions", handler.LastRequest!.RequestUri!.ToString());
        Assert.True(handler.LastRequest.Headers.Contains("api-key"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}")]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    public async Task Azure_ReturnsNullOnHttpFailure(HttpStatusCode status, string body)
    {
        var proposal = await Reasoner(new StubHandler(status, body))
            .ProposeAsync("greet", Catalog, CancellationToken.None);

        Assert.Null(proposal);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"choices":[]}""")]
    public async Task Azure_ReturnsNullOnUnusableEnvelope(string body)
    {
        var proposal = await Reasoner(new StubHandler(HttpStatusCode.OK, body))
            .ProposeAsync("greet", Catalog, CancellationToken.None);

        Assert.Null(proposal);
    }

    [Theory]
    [InlineData("""{"action_id":null}""")]
    [InlineData("""{"action_id":""}""")]
    [InlineData("""{"nonsense":true}""")]
    [InlineData("[1,2,3]")]
    public async Task Azure_ReturnsNullWhenModelDeclinesOrRambles(string content)
    {
        var proposal = await Reasoner(new StubHandler(HttpStatusCode.OK, Completion(content)))
            .ProposeAsync("greet", Catalog, CancellationToken.None);

        Assert.Null(proposal);
    }

    [Fact]
    public async Task Azure_UnknownActionIsStillProposed_ForTheKernelToReject()
    {
        // The adapter does not police the catalog; that is the kernel's job, and keeping the
        // proposal intact means the caller gets "unknown_action" instead of a silent no-op.
        var handler = new StubHandler(
            HttpStatusCode.OK, Completion("""{"action_id":"files.delete_everything","input":{}}"""));

        var proposal = await Reasoner(handler).ProposeAsync("wipe the disk", Catalog, CancellationToken.None);

        Assert.Equal("files.delete_everything", proposal!.ActionId);
    }

    // ---- composition ----

    private sealed class FixedReasoner : IReasoner
    {
        private readonly ReasonerProposal? _proposal;

        public FixedReasoner(ReasonerProposal? proposal) => _proposal = proposal;

        public int Calls { get; private set; }

        public ValueTask<ReasonerProposal?> ProposeAsync(
            string objective, IReadOnlyList<CapabilityDescriptor> catalog, CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromResult(_proposal);
        }
    }

    [Fact]
    public async Task Composite_FallsThroughToTheNextProposer()
    {
        var first = new FixedReasoner(null);
        var second = new FixedReasoner(new ReasonerProposal("clock.now", null, 0.4, ResolutionVia.Keyword));

        var proposal = await new CompositeReasoner([first, second])
            .ProposeAsync("now", Catalog, CancellationToken.None);

        Assert.Equal("clock.now", proposal!.ActionId);
        Assert.Equal(1, first.Calls);
    }

    [Fact]
    public async Task Composite_StopsAtTheFirstProposal()
    {
        var first = new FixedReasoner(new ReasonerProposal("echo.say", null, 0.9, ResolutionVia.Reasoner));
        var second = new FixedReasoner(new ReasonerProposal("clock.now", null, 0.4, ResolutionVia.Keyword));

        var proposal = await new CompositeReasoner([first, second])
            .ProposeAsync("greet", Catalog, CancellationToken.None);

        Assert.Equal("echo.say", proposal!.ActionId);
        Assert.Equal(0, second.Calls);
    }
}
