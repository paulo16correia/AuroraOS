using System.Text.Json;
using Aurora.Adapters.Consent;
using Aurora.Adapters.Persistence;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

public sealed class SessionAwareConsentGateTests
{
    private static readonly Principal Caller = new("c1", "u1");
    private static readonly JsonElement NoInput = JsonDocument.Parse("{}").RootElement;

    private static CapabilityDescriptor Capability(
        string actionId, RiskLevel risk, bool approvalRequired, params string[] effects) =>
        new(actionId, actionId, "test capability",
            JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone(),
            effects, risk, approvalRequired);

    private static readonly CapabilityDescriptor MediumRead =
        Capability("files.read_sandbox", RiskLevel.Medium, approvalRequired: true);

    private static readonly CapabilityDescriptor OtherMediumRead =
        Capability("vault.read", RiskLevel.Medium, approvalRequired: true);

    private static readonly CapabilityDescriptor MediumWrite =
        Capability("files.write_sandbox", RiskLevel.Medium, approvalRequired: true, "files.write");

    private static readonly CapabilityDescriptor HighRead =
        Capability("secrets.read", RiskLevel.High, approvalRequired: true);

    /// <summary>A capability that asks, in its own words, to cover repeated calls to one write.</summary>
    private static readonly CapabilityDescriptor Opener =
        Capability("voice.converse", RiskLevel.High, approvalRequired: true, "voice.speak") with
        {
            Reversible = true,
            OpensWindow = new SessionWindow(
                ["files.write_sandbox"], MaxActions: 3, Lifetime: TimeSpan.FromMinutes(10)),
        };

    private sealed class Harness : IDisposable
    {
        public SqliteTestDb Db { get; } = new();

        public FakeApprovalStore Approvals { get; } = new();

        public SqliteConsentSessionStore Sessions { get; }

        public SessionAwareConsentGate Gate { get; }

        public Harness()
        {
            Sessions = new SqliteConsentSessionStore(
                Db.Factory,
                new TestClock(DateTimeOffset.UnixEpoch),
                new FakeServerIdentity("boot-1"),
                new VersionedFakePolicy(true, "pv-1"),
                ConsentSessionOptions.Default);
            Gate = new SessionAwareConsentGate(Approvals, Sessions);
        }

        /// <summary>Runs the request → approve → retry cycle and returns the granting outcome.</summary>
        public async Task<ConsentOutcome> ApproveAndRetryAsync(CapabilityDescriptor capability, string scope)
        {
            var denied = await Gate.EvaluateAsync(capability, NoInput, scope, Caller, CancellationToken.None);
            Assert.False(denied.Granted);
            await Approvals.DecideAsync(Caller, denied.Info.ApprovalId!, approve: true, CancellationToken.None);
            return await Gate.EvaluateAsync(capability, NoInput, scope, Caller, CancellationToken.None);
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task LowRiskStillAutoGrants()
    {
        using var h = new Harness();

        var outcome = await h.Gate.EvaluateAsync(
            Capability("clock.now", RiskLevel.Low, approvalRequired: false),
            NoInput, "scope", Caller, CancellationToken.None);

        Assert.True(outcome.Granted);
        Assert.Equal(ConsentDecision.AutoLow, outcome.Info.Decision);
    }

    [Fact]
    public async Task ApprovingARead_OpensASessionThatCoversTheNextRead()
    {
        using var h = new Harness();

        var granted = await h.ApproveAndRetryAsync(MediumRead, "scope-1");
        Assert.True(granted.Granted);
        Assert.NotNull(granted.Info.SessionId);

        // A different read-only action, a different input: covered without a second prompt.
        var second = await h.Gate.EvaluateAsync(
            OtherMediumRead, NoInput, "scope-2", Caller, CancellationToken.None);

        Assert.True(second.Granted);
        Assert.Equal("session", second.Info.Via);
    }

    [Fact]
    public async Task ALiveSessionNeverCoversAWrite()
    {
        using var h = new Harness();

        // Open a session the legitimate way, by approving a read.
        await h.ApproveAndRetryAsync(MediumRead, "scope-read");
        Assert.Equal(1, await h.Sessions.CountActiveAsync(CancellationToken.None));

        var write = await h.Gate.EvaluateAsync(
            MediumWrite, NoInput, "scope-write", Caller, CancellationToken.None);

        // The owner's rule: an unnamed session is for reads. A write costs its own decision, bound
        // to its own input, unless some capability opened a window naming it — and approving a read
        // never does that.
        Assert.False(write.Granted);
        Assert.Equal(ConsentDecision.RequiresApproval, write.Info.Decision);
        Assert.Equal("approval", write.Info.Via);
    }

    [Fact]
    public async Task ApprovingAWrite_DoesNotOpenASession()
    {
        using var h = new Harness();

        var granted = await h.ApproveAndRetryAsync(MediumWrite, "scope-write");

        Assert.True(granted.Granted);
        Assert.Null(granted.Info.SessionId);
        Assert.Equal(0, await h.Sessions.CountActiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EachWriteNeedsItsOwnApproval()
    {
        using var h = new Harness();
        await h.ApproveAndRetryAsync(MediumWrite, "scope-write-1");

        var second = await h.Gate.EvaluateAsync(
            MediumWrite, NoInput, "scope-write-2", Caller, CancellationToken.None);

        Assert.False(second.Granted);
    }

    [Fact]
    public async Task HighRiskReadIsNeverCoveredBySession()
    {
        using var h = new Harness();
        await h.ApproveAndRetryAsync(MediumRead, "scope-read");

        // Read-only, but HIGH: reuse is capped at MEDIUM, because what makes it HIGH is usually
        // the sensitivity of what it reads.
        var high = await h.Gate.EvaluateAsync(HighRead, NoInput, "scope-high", Caller, CancellationToken.None);

        Assert.False(high.Granted);
        Assert.Equal("approval", high.Info.Via);
    }

    [Fact]
    public async Task RevokingTheSession_SendsReadsBackToApproval()
    {
        using var h = new Harness();
        await h.ApproveAndRetryAsync(MediumRead, "scope-read");

        await h.Sessions.RevokeAllAsync(CancellationToken.None);

        var after = await h.Gate.EvaluateAsync(
            OtherMediumRead, NoInput, "scope-2", Caller, CancellationToken.None);

        Assert.False(after.Granted);
        Assert.Equal(ConsentDecision.RequiresApproval, after.Info.Decision);
    }

    [Fact]
    public async Task ApprovingAWindow_CoversTheWriteItNamed()
    {
        using var h = new Harness();

        var opened = await h.ApproveAndRetryAsync(Opener, "scope-open");
        Assert.True(opened.Granted);
        Assert.NotNull(opened.Info.SessionId);

        // The write the window named, with no second prompt — which is the whole point of asking
        // about it once, in words, instead of once per sentence.
        var write = await h.Gate.EvaluateAsync(
            MediumWrite, NoInput, "scope-write", Caller, CancellationToken.None);

        Assert.True(write.Granted);
        Assert.Equal("session", write.Info.Via);
        Assert.Equal(opened.Info.SessionId, write.Info.SessionId);
    }

    [Fact]
    public async Task AWindowCoversNothingItDidNotName()
    {
        using var h = new Harness();
        await h.ApproveAndRetryAsync(Opener, "scope-open");

        // Another write at the same risk, from the same caller, while the window is live. The
        // window named one action; everything else costs what it always cost.
        var other = await h.Gate.EvaluateAsync(
            Capability("vault.write", RiskLevel.Medium, approvalRequired: true, "vault.write"),
            NoInput, "scope-other", Caller, CancellationToken.None);

        Assert.False(other.Granted);
        Assert.Equal(ConsentDecision.RequiresApproval, other.Info.Decision);

        // Not even a read: a window opened for speaking is not a window for reading either, or
        // "covers what it named" would quietly mean "covers that and more".
        var read = await h.Gate.EvaluateAsync(
            MediumRead, NoInput, "scope-read", Caller, CancellationToken.None);

        Assert.False(read.Granted);
    }

    [Fact]
    public async Task AWindowIsSpentByItsBudget()
    {
        using var h = new Harness();
        await h.ApproveAndRetryAsync(Opener, "scope-open");

        for (var i = 0; i < 3; i++)
        {
            var used = await h.Gate.EvaluateAsync(
                MediumWrite, NoInput, $"scope-{i}", Caller, CancellationToken.None);
            Assert.True(used.Granted);
        }

        var fourth = await h.Gate.EvaluateAsync(
            MediumWrite, NoInput, "scope-4", Caller, CancellationToken.None);

        Assert.False(fourth.Granted);
        Assert.Equal(ConsentDecision.RequiresApproval, fourth.Info.Decision);
    }

    [Fact]
    public async Task RevokingEndsTheWindowLikeAnyOtherSession()
    {
        using var h = new Harness();
        await h.ApproveAndRetryAsync(Opener, "scope-open");

        await h.Sessions.RevokeAllAsync(CancellationToken.None);

        var after = await h.Gate.EvaluateAsync(
            MediumWrite, NoInput, "scope-write", Caller, CancellationToken.None);

        Assert.False(after.Granted);
    }

    [Fact]
    public async Task CapabilityNotMarkedApprovalRequired_StaysRefused()
    {
        using var h = new Harness();

        var outcome = await h.Gate.EvaluateAsync(
            Capability("vault.write", RiskLevel.Medium, approvalRequired: false, "writes"),
            NoInput, "scope", Caller, CancellationToken.None);

        Assert.False(outcome.Granted);
        Assert.Equal("policy", outcome.Info.Via);
    }
}
