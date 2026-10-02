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
        new("vs-1", VoiceChannel.Discord, "fake",
            new VoiceParticipant("somebody"), grant ?? Grant(), state,
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
                "paulo", "Paulo", "identity/owner",
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

    // Everything about placing a call used to be here: an intent with a purpose and an approval,
    // an allowlist of destinations, and the two switches that governed them. It went with the
    // telephone. The one rule in it that was not about telephones — the concurrency limit, which is
    // across every channel — moved to LocalVoiceTests, where the check it guards now lives.

    [Fact]
    public void NothingIsEnabledOnAnInstallationNobodyHasConfigured()
    {
        VoiceSettings settings = VoiceSettings.Default;

        // An install that listened and spoke before its owner had decided it should is one that
        // made a decision on their behalf.
        Assert.False(settings.Enabled);

        // And a ceiling that is not infinity. An install whose sessions could last as long as a
        // grant asked for would have a limit only on paper.
        Assert.Equal(TimeSpan.FromMinutes(15), settings.MaxSessionDuration);
    }
}
