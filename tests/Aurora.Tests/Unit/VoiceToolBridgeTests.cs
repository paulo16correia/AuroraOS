using System.Text.Json;
using Aurora.Adapters.Observability;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Presence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Kernel;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// What happens when the voice interaction layer asks Aurora for something (docs/adr/0073).
/// </summary>
/// <remarks>
/// These run a real <see cref="AuroraKernel"/> rather than a stand-in for it, because the claim
/// being tested is precisely that voice does not get its own authority path. A test against a fake
/// kernel would prove that the bridge calls something; this proves that what it calls refuses the
/// same things Aurora refuses everywhere else.
/// </remarks>
public sealed class VoiceToolBridgeTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-02T10:00:00Z");
    private static readonly Principal Caller = new("voice", "aurora");

    private readonly SqliteTestDb _db = new();
    private readonly TestClock _clock = new(Now);
    private readonly SqliteVoiceSessionStore _sessions;
    private readonly RecordingAuditStore _audit = new();

    public VoiceToolBridgeTests()
    {
        _sessions = new SqliteVoiceSessionStore(_db.Factory, _clock);
    }

    public void Dispose() => _db.Dispose();

    /// <summary>A kernel whose policy and consent answer as told, so refusals can be provoked.</summary>
    private AuroraKernel Kernel(bool policyAllows = true, bool consentAllows = true) =>
        new(new FakeReasoner(null),
            new FakeRegistry(Recall()),
            new FakeValidator(true),
            new FakePolicy(policyAllows),
            new FakeConsent(consentAllows),
            new FakeApprovalStore(),
            new DirectExecutor(),
            _audit,
            new InMemoryIdempotencyStore(),
            new InMemoryMetrics(_clock),
            new FakePassphrase(),
            TestBus.Over(_db.Factory, _clock),
            new NoOperatorPrompt());

    /// <summary>A capability that reads and nothing more, so the refusals under test are the
    /// voice layer's and the Kernel's rather than the capability's own.</summary>
    private static FakeCapability Recall() =>
        new(FakeCapability.LowReadOnly("memory.recall", """{"type":"object"}"""),
            _ => JsonSerializer.SerializeToElement(
                new Dictionary<string, string> { ["recalled"] = "something" }));

    private VoiceToolBridge Bridge(AuroraKernel kernel, VoiceSettings? settings = null) =>
        new(_sessions,
            new VoicePolicyService(settings ?? VoiceSettings.Default, _sessions, _audit),
            kernel, _clock, Caller);

    private async Task<VoiceSession> OpenAsync(
        string[]? actions = null, int maxCalls = 5, VoiceSessionState state = VoiceSessionState.Active)
    {
        var session = new VoiceSession(
            "vs-1", VoiceChannel.Phone, "fake", VoiceCallDirection.Inbound,
            new VoiceParticipant("+351911111111"),
            new VoiceGrant(actions ?? ["memory.recall"], maxCalls, TimeSpan.FromMinutes(10),
                Now.AddMinutes(30).ToString("O")),
            state, Now.ToString("O"), "corr-1");

        return await _sessions.OpenAsync(session, CancellationToken.None);
    }

    private static VoiceToolContext Request(string action = "memory.recall", string input = "{}") =>
        new("vs-1", "corr-1", "req-1", action, input);

    // ---- the ordinary path ----

    [Fact]
    public async Task AGrantedActionThePolicyAllowsRuns()
    {
        await OpenAsync();

        VoiceToolOutcome outcome = await Bridge(Kernel())
            .RunAsync(Request(), CancellationToken.None);

        Assert.Equal(VoiceToolResult.Completed, outcome.Outcome);
        Assert.NotNull(outcome.ResultJson);
    }

    [Fact]
    public async Task RunningOneSpendsOneOfTheSessionsCalls()
    {
        await OpenAsync(maxCalls: 2);
        VoiceToolBridge bridge = Bridge(Kernel());

        await bridge.RunAsync(Request(), CancellationToken.None);

        VoiceSession after = (await _sessions.FindAsync("vs-1", CancellationToken.None))!;
        Assert.Equal(1, after.ToolCallsUsed);
    }

    // ---- Realtime cannot get past the Kernel ----

    [Fact]
    public async Task WhatThePolicyRefusesTheVoiceLayerCannotDo()
    {
        await OpenAsync();

        // The session's own grant names this action, so the bridge lets it through — and the
        // Kernel refuses it anyway. That is the property: voice has no separate authority path.
        VoiceToolOutcome outcome = await Bridge(Kernel(policyAllows: false))
            .RunAsync(Request(), CancellationToken.None);

        Assert.Equal(VoiceToolResult.Refused, outcome.Outcome);
        Assert.Equal(VoiceRefusal.KernelRefused, outcome.Refusal);
    }

    [Fact]
    public async Task WhatNeedsAnApprovalIsRefusedOnACallRatherThanQuietlyAllowed()
    {
        await OpenAsync();

        VoiceToolOutcome outcome = await Bridge(Kernel(consentAllows: false))
            .RunAsync(Request(), CancellationToken.None);

        // Somebody on a telephone cannot approve something. Being told so is the correct outcome,
        // and it is the same consent gate every other channel meets.
        Assert.Equal(VoiceToolResult.Refused, outcome.Outcome);
        Assert.Equal(VoiceRefusal.KernelRefused, outcome.Refusal);
    }

    [Fact]
    public async Task AnActionOutsideTheGrantNeverReachesTheKernelAtAll()
    {
        await OpenAsync(actions: ["memory.recall"]);

        VoiceToolOutcome outcome = await Bridge(Kernel())
            .RunAsync(Request("files.write_sandbox"), CancellationToken.None);

        Assert.Equal(VoiceRefusal.NotInGrant, outcome.Refusal);

        // Nothing was executed and nothing was charged: the session's ceiling is checked before
        // the Kernel is troubled, so a call cannot burn its budget probing for capabilities.
        VoiceSession after = (await _sessions.FindAsync("vs-1", CancellationToken.None))!;
        Assert.Equal(0, after.ToolCallsUsed);
    }

    [Fact]
    public async Task ARequestNamingASessionAuroraDoesNotHoldIsRefused()
    {
        VoiceToolOutcome outcome = await Bridge(Kernel())
            .RunAsync(Request(), CancellationToken.None);

        // The session is looked up, never taken from the message. A request that invented one gets
        // nothing, rather than being decided against some default.
        Assert.Equal(VoiceToolResult.Refused, outcome.Outcome);
        Assert.Equal(VoiceRefusal.NotLive, outcome.Refusal);
    }

    [Fact]
    public async Task StoppingVoiceRefusesTheNextToolRequest()
    {
        await OpenAsync();

        var policy = new VoicePolicyService(VoiceSettings.Default, _sessions, _audit);
        var bridge = new VoiceToolBridge(_sessions, policy, Kernel(), _clock, Caller);

        await policy.StopAsync("operator", "enough", CancellationToken.None);

        VoiceToolOutcome outcome = await bridge.RunAsync(Request(), CancellationToken.None);

        Assert.Equal(VoiceRefusal.VoiceStopped, outcome.Refusal);
    }

    [Fact]
    public async Task ASpentBudgetRefusesWithoutExecutingAnything()
    {
        await OpenAsync(maxCalls: 1);
        VoiceToolBridge bridge = Bridge(Kernel());

        Assert.Equal(
            VoiceToolResult.Completed,
            (await bridge.RunAsync(Request(), CancellationToken.None)).Outcome);

        VoiceToolOutcome second = await bridge.RunAsync(
            new VoiceToolContext("vs-1", "corr-1", "req-2", "memory.recall", "{}"),
            CancellationToken.None);

        Assert.Equal(VoiceRefusal.BudgetSpent, second.Refusal);
    }

    // ---- what the interaction layer is allowed to say afterwards ----

    [Fact]
    public async Task ArgumentsThatAreNotJsonAreAFailureAndNotASuccess()
    {
        await OpenAsync();

        VoiceToolOutcome outcome = await Bridge(Kernel())
            .RunAsync(Request(input: "{ this is not json"), CancellationToken.None);

        Assert.Equal(VoiceToolResult.Failed, outcome.Outcome);
    }

    [Fact]
    public async Task MissingArgumentsAreReadAsAnEmptyObjectRatherThanFailing()
    {
        await OpenAsync();

        VoiceToolOutcome outcome = await Bridge(Kernel())
            .RunAsync(Request(input: ""), CancellationToken.None);

        Assert.Equal(VoiceToolResult.Completed, outcome.Outcome);
    }

    [Fact]
    public async Task EveryOutcomeIsOneOfTheFourAndRefusalIsNotFailure()
    {
        await OpenAsync();

        VoiceToolOutcome allowed = await Bridge(Kernel())
            .RunAsync(Request(), CancellationToken.None);
        VoiceToolOutcome refused = await Bridge(Kernel(policyAllows: false))
            .RunAsync(
                new VoiceToolContext("vs-1", "corr-1", "req-2", "memory.recall", "{}"),
                CancellationToken.None);

        // Kept apart on purpose. A model asked to narrate an outcome narrates a plausible one
        // unless it is handed an unambiguous one, and "Aurora would not do that" and "it did not
        // work" are different things to be told on a telephone.
        Assert.Equal(VoiceToolResult.Completed, allowed.Outcome);
        Assert.Equal(VoiceToolResult.Refused, refused.Outcome);
        Assert.NotEqual(VoiceToolResult.Failed, refused.Outcome);
    }

    [Fact]
    public async Task EveryToolRequestReachesTheOrdinaryAudit()
    {
        await OpenAsync();

        await Bridge(Kernel()).RunAsync(Request(), CancellationToken.None);

        // Through the same chain as every other action, because a voice call doing something is
        // the same kind of event as anything else doing it.
        Assert.Contains(_audit.Entries, e => e.ActionId == "memory.recall");
    }

    [Fact]
    public async Task ARefusedRequestIsAuditedAsARefusalRatherThanNotAtAll()
    {
        await OpenAsync();

        await Bridge(Kernel(policyAllows: false)).RunAsync(Request(), CancellationToken.None);

        Assert.Contains(
            _audit.Entries,
            e => e.ActionId == "memory.recall" && e.Outcome != "completed");
    }

    // ---- one call's request is never decided against another call's authority ----

    [Fact]
    public async Task TwoLiveSessionsKeepSeparateGrantsAndSeparateBudgets()
    {
        await OpenAsync(actions: ["memory.recall"], maxCalls: 1);

        await _sessions.OpenAsync(
            new VoiceSession(
                "vs-2", VoiceChannel.Discord, "discord", VoiceCallDirection.Inbound,
                new VoiceParticipant("user-2"),
                new VoiceGrant([], 5, TimeSpan.FromMinutes(10), Now.AddMinutes(30).ToString("O")),
                VoiceSessionState.Active, Now.ToString("O"), "corr-2"),
            CancellationToken.None);

        VoiceToolBridge bridge = Bridge(Kernel());

        VoiceToolOutcome first = await bridge.RunAsync(Request(), CancellationToken.None);

        // The second session's grant names nothing. Its request is refused even though another
        // live session at that moment could have made it.
        VoiceToolOutcome second = await bridge.RunAsync(
            new VoiceToolContext("vs-2", "corr-2", "req-2", "memory.recall", "{}"),
            CancellationToken.None);

        Assert.Equal(VoiceToolResult.Completed, first.Outcome);
        Assert.Equal(VoiceRefusal.NotInGrant, second.Refusal);
    }
}
