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

        // The owner's rule: reuse is for reads. A write always costs its own decision, bound to
        // its own input, or a single approval would become standing authority to keep writing.
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
