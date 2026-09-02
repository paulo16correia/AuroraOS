using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Presence;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// What a voice session may do, and the many things that do not decide it (docs/adr/0073).
/// </summary>
/// <remarks>
/// Most of this file is refusals, and that is the point. The dangerous version of a voice layer is
/// one where a convincing sentence, a familiar caller or a standing mission quietly widens what a
/// call can reach — and every one of those is a path that only gets tested if the decision is a
/// pure function somebody can call from a test.
/// </remarks>
public sealed class VoiceAuthorizationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-02T10:00:00Z");

    private static VoiceGrant Grant(
        string[]? actions = null, int maxCalls = 5, int minutesValid = 30) =>
        new(actions ?? ["memory.recall"], maxCalls, TimeSpan.FromMinutes(20),
            Now.AddMinutes(minutesValid).ToString("O"));

    private static VoiceSession Session(
        VoiceGrant? grant = null,
        VoiceSessionState state = VoiceSessionState.Active,
        int used = 0,
        DateTimeOffset? started = null) =>
        new("vs-1", VoiceChannel.Phone, "fake", VoiceCallDirection.Inbound,
            new VoiceParticipant("+351911111111"), grant ?? Grant(), state,
            (started ?? Now).ToString("O"), "corr-1", ToolCallsUsed: used);

    // ---- the ordinary case ----

    [Fact]
    public void AnActionTheGrantNamesIsAllowedThrough()
    {
        VoiceDecision decision = VoiceAuthorization.ForTool(
            Session(), "memory.recall", Now, voiceStopped: false);

        // "Allowed" here means the session's own ceiling permits asking. The Kernel decides
        // afterwards, against policy and approval, and may still refuse — which is the ordinary
        // case rather than a bug.
        Assert.True(decision.Allowed);
    }

    // ---- the refusals ----

    [Fact]
    public void AnActionTheGrantDoesNotNameIsRefusedHoweverItIsAsked()
    {
        VoiceDecision decision = VoiceAuthorization.ForTool(
            Session(), "mail.send", Now, voiceStopped: false);

        Assert.False(decision.Allowed);
        Assert.Equal(VoiceRefusal.NotInGrant, decision.Refusal);
    }

    [Fact]
    public void AStoppedVoiceRefusesBeforeAnythingElseIsConsidered()
    {
        // Checked first on purpose. A stop that could be out-argued by a valid grant, a live
        // session and a remaining budget would not be a stop.
        VoiceDecision decision = VoiceAuthorization.ForTool(
            Session(), "memory.recall", Now, voiceStopped: true);

        Assert.False(decision.Allowed);
        Assert.Equal(VoiceRefusal.VoiceStopped, decision.Refusal);
    }

    [Theory]
    [InlineData(VoiceSessionState.Ended)]
    [InlineData(VoiceSessionState.Failed)]
    [InlineData(VoiceSessionState.Cancelled)]
    public void ASessionThatIsOverCanAskForNothing(VoiceSessionState state)
    {
        VoiceDecision decision = VoiceAuthorization.ForTool(
            Session(state: state), "memory.recall", Now, voiceStopped: false);

        Assert.False(decision.Allowed);
        Assert.Equal(VoiceRefusal.NotLive, decision.Refusal);
    }

    [Fact]
    public void ASpentBudgetRefusesTheNextRequest()
    {
        VoiceDecision decision = VoiceAuthorization.ForTool(
            Session(Grant(maxCalls: 3), used: 3), "memory.recall", Now, voiceStopped: false);

        Assert.False(decision.Allowed);
        Assert.Equal(VoiceRefusal.BudgetSpent, decision.Refusal);
    }

    [Fact]
    public void AnExpiredGrantStopsWorkingMidCall()
    {
        VoiceDecision decision = VoiceAuthorization.ForTool(
            Session(Grant(minutesValid: 5)), "memory.recall",
            Now.AddMinutes(10), voiceStopped: false);

        Assert.False(decision.Allowed);
        Assert.Equal(VoiceRefusal.Expired, decision.Refusal);
    }

    [Fact]
    public void ACallThatRunsPastItsMaximumDurationLosesItsAuthority()
    {
        // Two clocks, and this is the second: the grant is still inside its window, and the call
        // itself has run longer than the decision that authorised it contemplated.
        VoiceDecision decision = VoiceAuthorization.ForTool(
            Session(Grant(minutesValid: 600), started: Now), "memory.recall",
            Now.AddMinutes(45), voiceStopped: false);

        Assert.False(decision.Allowed);
        Assert.Equal(VoiceRefusal.Expired, decision.Refusal);
    }

    [Fact]
    public void AnUnreadableExpiryIsTreatedAsPassed()
    {
        VoiceGrant broken = Grant() with { ExpiresAtUtc = "whenever" };

        // A grant whose limits cannot be read is one whose limits are unknown, and the safe
        // reading of an unknown limit is that it has been reached.
        Assert.False(
            VoiceAuthorization.ForTool(Session(broken), "memory.recall", Now, false).Allowed);
    }

    // ---- the things that are not authorisation ----
    //
    // Each of these is a sentence somebody will say on a call, and none of them is an input to the
    // decision. The tests exist because the way this goes wrong is a parameter being added "just
    // for context" and then being read.

    [Fact]
    public void BeingKnownToAuroraGrantsNothing()
    {
        VoiceSession familiar = Session() with
        {
            Participant = new VoiceParticipant(
                "+351911111111", "Paulo", "identity/owner",
                ParticipantVerification.ChannelAuthenticated),
        };

        // "You know me, so you can send it." The participant is fully resolved, authenticated by
        // the channel, and known — and the grant still says what it said.
        VoiceDecision decision = VoiceAuthorization.ForTool(
            familiar, "mail.send", Now, voiceStopped: false);

        Assert.False(decision.Allowed);
        Assert.Equal(VoiceRefusal.NotInGrant, decision.Refusal);
    }

    [Fact]
    public void TheDecisionCannotSeeARelationshipAMemoryOrAMission()
    {
        // Asserted over the signature rather than over behaviour, because behaviour can only show
        // that today's code ignores them. There is no parameter for a relationship, a memory, a
        // mission or a plan — so no future edit can start reading one without changing this.
        var parameters = typeof(VoiceAuthorization)
            .GetMethod(nameof(VoiceAuthorization.ForTool))!
            .GetParameters()
            .Select(p => p.Name!.ToLowerInvariant())
            .ToArray();

        Assert.Equal(["session", "actionid", "nowutc", "voicestopped"], parameters);
    }

    // ---- placing a call at all ----

    private static OutboundCallIntent Intent(
        string target = "+351911111111", string approval = "ap-1", int minutesValid = 30) =>
        new("Remind about tomorrow's meeting",
            "Confirm they know the time",
            new VoiceParticipant(target),
            Grant(["memory.recall"], minutesValid: minutesValid),
            ["do not discuss anything else"],
            "operator",
            approval);

    /// <summary>
    /// Judges an outbound call. <paramref name="intent"/> is passed through exactly as given,
    /// including null — an earlier version defaulted it, which meant the one test that needed a
    /// missing intent silently sent a valid one and passed for the wrong reason.
    /// </summary>
    private static VoiceDecision Outbound(
        OutboundCallIntent? intent,
        bool stopped = false,
        bool enabled = true,
        string[]? allowed = null,
        int live = 0,
        int max = 2,
        DateTimeOffset? now = null) =>
        VoiceAuthorization.ForOutboundCall(
            intent, now ?? Now, stopped, enabled, allowed ?? ["+351"], live, max);

    [Fact]
    public void AnAuthorisedOutboundCallIsAllowed()
    {
        Assert.True(Outbound(Intent()).Allowed);
    }

    [Fact]
    public void ThereIsNoOutboundCallWithoutAnIntent()
    {
        // The whole rule in one branch. A mission that produced a goal and a planner that produced
        // a task both arrive here with nothing to show, and are refused.
        VoiceDecision decision = Outbound(intent: null);

        Assert.False(decision.Allowed);
        Assert.Contains("neither a mission nor a plan", decision.Detail);
    }

    [Fact]
    public void AnIntentWithNoApprovalIsNotAnAuthorisation()
    {
        Assert.False(Outbound(Intent(approval: "")).Allowed);
    }

    [Fact]
    public void AnIntentWithNoStatedPurposeIsRefused()
    {
        // A purpose nobody wrote is a purpose nobody read, and the person approving has to have
        // been shown what the call was for.
        OutboundCallIntent blank = Intent() with { Purpose = "  " };

        Assert.False(Outbound(blank).Allowed);
    }

    [Fact]
    public void AnExpiredAuthorisationDoesNotCarryForward()
    {
        VoiceDecision decision = Outbound(Intent(minutesValid: 5), now: Now.AddMinutes(10));

        Assert.False(decision.Allowed);
        Assert.Equal(VoiceRefusal.Expired, decision.Refusal);
    }

    [Fact]
    public void OutboundIsOffUntilSomebodyTurnsItOn()
    {
        // Having a number is not a decision to ring people with it, and the two are configured
        // separately for exactly that reason.
        Assert.False(Outbound(Intent(), enabled: false).Allowed);
    }

    [Fact]
    public void AnEmptyDestinationListAllowsNothing()
    {
        // Empty meaning "anywhere" would make an unconfigured install able to dial the world.
        Assert.False(Outbound(Intent(), allowed: []).Allowed);
    }

    [Fact]
    public void ADestinationOutsideThePolicyIsRefused()
    {
        Assert.False(Outbound(Intent(target: "+15551234567"), allowed: ["+351"]).Allowed);
    }

    [Fact]
    public void APortugueseNumberIsAllowedByItsPrefix()
    {
        Assert.True(VoiceAuthorization.Permitted("+351911111111", ["+351"]));
        Assert.True(VoiceAuthorization.Permitted("+351211234567", ["+351911111111", "+351"]));
    }

    [Fact]
    public void AWholeNumberInThePolicyMatchesOnlyThatNumber()
    {
        Assert.True(VoiceAuthorization.Permitted("+351911111111", ["+351911111111"]));
        Assert.False(VoiceAuthorization.Permitted("+351911111112", ["+351911111111"]));
    }

    [Fact]
    public void ANumberThatMerelyContainsAnAllowedOneIsNotAllowed()
    {
        // Prefix matching is anchored. Somewhere there is a number ending in the digits of a
        // permitted one, and it is not the permitted one.
        Assert.False(VoiceAuthorization.Permitted("+9991351911111111", ["+351"]));
    }

    [Fact]
    public void TheConcurrencyLimitIsAcrossEveryChannelAndRefusesTheNextCall()
    {
        VoiceDecision decision = Outbound(Intent(), live: 2, max: 2);

        Assert.False(decision.Allowed);
        Assert.Equal(VoiceRefusal.BudgetSpent, decision.Refusal);
    }

    [Fact]
    public void AStoppedVoiceRefusesToPlaceACallAtAll()
    {
        Assert.False(Outbound(Intent(), stopped: true).Allowed);
    }

    [Fact]
    public void NothingIsEnabledOnAnInstallationNobodyHasConfigured()
    {
        VoiceSettings settings = VoiceSettings.Default;

        // An install that answered the telephone before its owner had decided it should is one
        // that made a decision on their behalf.
        Assert.False(settings.InboundEnabled);
        Assert.False(settings.OutboundEnabled);
        Assert.Empty(settings.AllowedDestinations);
    }
}
