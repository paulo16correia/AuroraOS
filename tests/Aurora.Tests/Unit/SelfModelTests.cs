using System.Globalization;
using System.Text.Json;
using Aurora.Adapters.Cognition;
using Aurora.Adapters.Events;
using Aurora.Adapters.Operations;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Resources;
using Aurora.Adapters.Scheduling;
using Aurora.Adapters.Self;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Self (RFC 027): what Aurora can actually do right now, observed rather than assumed.
/// </summary>
public sealed class SelfModelTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly Principal Caller = new("local-mcp-client", "paulo");

    private const string Schema =
        """{"type":"object","additionalProperties":false,"properties":{"message":{"type":"string"}}}""";

    private static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static FakeCapability Reader() =>
        new(FakeCapability.LowReadOnly("clock.now", Schema), _ => JsonDocument.Parse("{}").RootElement);

    /// <summary>A capability that reaches outside Aurora — the interesting half of "I can prepare, but not send".</summary>
    private static FakeCapability Sender() =>
        new(
            new CapabilityDescriptor(
                "mail.send", "Send mail", "sends a message outside Aurora",
                JsonDocument.Parse(Schema).RootElement.Clone(),
                ["network.egress"], RiskLevel.Medium, true),
            _ => JsonDocument.Parse("{}").RootElement);

    private static SqliteSelfModel Build(
        SqliteTestDb db, TestClock clock, FakeResourceProbe? probe = null,
        IPolicyEngine? policy = null, IIdempotencyStore? idempotency = null)
    {
        var bus = new SqliteEventBus(db.Factory, new SqliteOutbox(new PermissiveEventCatalogue(), clock), clock);
        var cycles = new SqliteCognitiveCycle(db.Factory, clock);
        var audit = new SqliteAuditStore(
            db.Factory, clock, new byte[32],
            new AuditAnchorFile(Path.Combine(Path.GetTempPath(), $"a-{Guid.NewGuid():N}")));
        var resources = new SystemResourceModel(probe ?? new FakeResourceProbe(), clock);

        var health = new AuroraHealthService(
            db.Factory, audit, bus, resources, new AuditClockGuard(audit, clock),
            new SqliteScheduler(db.Factory, bus, cycles, clock), clock);

        return new SqliteSelfModel(
            db.Factory,
            new Adapters.Capabilities.StaticCapabilityRegistry([Reader(), Sender()]),
            policy ?? new FakePolicy(true), resources, health,
            idempotency ?? new InMemoryIdempotencyStore(), clock, TestBus.Over(db.Factory, clock));
    }

    // ---- rule 2: three questions, three answers ----

    [Fact]
    public async Task SomethingNotInstalledIsNotPermittedAndNotSafeEither()
    {
        using var db = new SqliteTestDb();
        SqliteSelfModel self = Build(db, new TestClock(At("2026-01-15T09:00:00+00:00")));

        CapabilityAssessment assessment = await self.CanAsync("nothing.like.this", Caller, Ct);

        Assert.False(assessment.Installed);
        Assert.False(assessment.Available);
        Assert.Equal("not installed", assessment.Reason);
    }

    [Fact]
    public async Task InstalledAndRefusedByPolicyIsStillInstalled()
    {
        using var db = new SqliteTestDb();
        SqliteSelfModel self = Build(
            db, new TestClock(At("2026-01-15T09:00:00+00:00")), policy: new FakePolicy(allow: false));

        CapabilityAssessment assessment = await self.CanAsync("clock.now", Caller, Ct);

        // Installed and not permitted are different facts, and saying "I cannot do that" when the
        // truth is "I am not allowed to" is a different answer to a different question.
        Assert.True(assessment.Installed);
        Assert.False(assessment.Permitted);
        Assert.False(assessment.Available);
    }

    [Fact]
    public async Task EverythingLinesUpOnlyWhenAllThreeAreTrue()
    {
        using var db = new SqliteTestDb();
        SqliteSelfModel self = Build(db, new TestClock(At("2026-01-15T09:00:00+00:00")));

        CapabilityAssessment assessment = await self.CanAsync("clock.now", Caller, Ct);

        Assert.True(assessment.Installed);
        Assert.True(assessment.Permitted);
        Assert.True(assessment.SafeNow);
        Assert.True(assessment.Available);
    }

    // ---- "I can prepare, but not send" ----

    [Fact]
    public async Task ADegradedInstanceStillReadsAndWillNotReachOutside()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));

        // A machine under real pressure. Reading is unaffected; acting on the world is not.
        SqliteSelfModel self = Build(db, clock, new FakeResourceProbe(disk: 0.99));

        SelfModel model = await self.RefreshAsync("local", Ct);
        Assert.Equal(OperationalState.Degraded, model.OperationalState);

        CapabilityAssessment reading = await self.CanAsync("clock.now", Caller, Ct);
        CapabilityAssessment sending = await self.CanAsync("mail.send", Caller, Ct);

        Assert.True(reading.SafeNow);
        Assert.False(sending.SafeNow);
        Assert.Contains("outside Aurora", sending.Reason, StringComparison.Ordinal);

        // Both are installed and permitted. Only one is safe, which is the distinction rule 2 is
        // about and the sentence the RFC's justification asks Aurora to be able to say.
        Assert.True(sending.Installed);
        Assert.True(sending.Permitted);
    }

    // ---- limit case: recovering after a restart ----

    [Fact]
    public async Task AnInstanceWithCallsLeftIndeterminateIsRecoveringAndNotReady()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));
        // The real store, not the fake: the fake stubs reconciliation, and a test about what a
        // restart leaves behind has to use the thing that actually knows.
        var idempotency = new SqliteIdempotencyStore(db.Factory, clock);

        // A reservation the last process left in the air.
        await idempotency.BeginAsync(Caller, "k1", "hash", Ct);
        await idempotency.MarkExecutingAsync(Caller, "k1", Ct);
        clock.UtcNow = At("2026-01-15T10:00:00+00:00");
        await idempotency.ReconcileStaleAsync(TimeSpan.FromMinutes(15), Ct);

        SqliteSelfModel self = Build(db, clock, idempotency: idempotency);
        SelfModel model = await self.RefreshAsync("local", Ct);

        // A restart that left calls indeterminate has not finished starting, whatever the process
        // thinks about itself.
        Assert.Equal(OperationalState.Recovering, model.OperationalState);

        CapabilityAssessment assessment = await self.CanAsync("clock.now", Caller, Ct);
        Assert.False(assessment.SafeNow);
        Assert.Contains("recovering", assessment.Reason, StringComparison.Ordinal);
    }

    // ---- rule 4: health is observed and dated, never presumed ----

    [Fact]
    public async Task HealthCarriesWhenItWasObserved()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));

        SelfModel model = await Build(db, clock).RefreshAsync("local", Ct);

        Assert.Equal(At("2026-01-15T09:00:00+00:00"), DateTimeOffset.Parse(model.HealthObservedAtUtc));
        Assert.False(string.IsNullOrWhiteSpace(model.HealthSummary));
    }

    [Fact]
    public async Task AStaleReadingIsRefreshedRatherThanTrusted()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));
        var probe = new FakeResourceProbe();
        SqliteSelfModel self = Build(db, clock, probe);

        await self.RefreshAsync("local", Ct);

        // The world moved while the reading sat there. Permissions and health are exactly what
        // changes between readings, so a stale one is not even a starting guess.
        probe.Disk = 0.99;
        clock.UtcNow = At("2026-01-15T09:30:00+00:00");

        CapabilityAssessment sending = await self.CanAsync("mail.send", Caller, Ct);

        Assert.False(sending.SafeNow);
        Assert.Equal(OperationalState.Degraded, (await self.CurrentAsync(Ct))!.OperationalState);
    }

    // ---- rule 3: what Aurora says about itself ----

    [Fact]
    public async Task WhatAuroraSaysAboutItselfHasNowhereToPutASecret()
    {
        using var db = new SqliteTestDb();
        SqliteSelfModel self = Build(db, new TestClock(At("2026-01-15T09:00:00+00:00")));

        SafeSelfDescription described = await self.DescribeAsync(
            new MemoryAccessContext("owner", [MemoryAccessPolicy.Owner], Sensitivity.Private), Ct);

        Assert.NotEmpty(described.CanDo);
        Assert.False(string.IsNullOrWhiteSpace(described.OperationalState));

        // A separate type rather than a filtered SelfModel, because filtering is something somebody
        // forgets. There is no field here that could carry a secret or a hostname.
        var fields = typeof(SafeSelfDescription).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain("IdentityRef", fields);
        Assert.DoesNotContain("Resources", fields);
        Assert.DoesNotContain("Capabilities", fields);
    }

    // ---- pausing is a decision, and an observation does not overturn it ----

    [Fact]
    public async Task APausedInstanceStaysPausedThroughARefresh()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));
        SqliteSelfModel self = Build(db, clock);

        await self.RefreshAsync("local", Ct);
        SelfModel paused = await self.PauseAsync("paulo", "going away for the weekend", Ct);

        Assert.Equal(OperationalState.Paused, paused.OperationalState);
        Assert.Contains("paulo", paused.PausedReason!, StringComparison.Ordinal);

        // Pausing is a decision somebody made. An observation is not entitled to overturn it.
        SelfModel refreshed = await self.RefreshAsync("local", Ct);
        Assert.Equal(OperationalState.Paused, refreshed.OperationalState);

        CapabilityAssessment assessment = await self.CanAsync("clock.now", Caller, Ct);
        Assert.False(assessment.SafeNow);
        Assert.Contains("weekend", assessment.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PausingRecordsWhoAndWhy()
    {
        using var db = new SqliteTestDb();
        SqliteSelfModel self = Build(db, new TestClock(At("2026-01-15T09:00:00+00:00")));

        await Assert.ThrowsAsync<SelfException>(() => self.PauseAsync("paulo", "", Ct));
        await Assert.ThrowsAsync<SelfException>(() => self.PauseAsync("", "some reason", Ct));
    }

    [Fact]
    public async Task ResumingTakesAFreshReadingRatherThanAssertingEverythingIsFine()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(At("2026-01-15T09:00:00+00:00"));
        var probe = new FakeResourceProbe();
        SqliteSelfModel self = Build(db, clock, probe);

        await self.RefreshAsync("local", Ct);
        await self.PauseAsync("paulo", "maintenance", Ct);

        probe.Disk = 0.99;
        SelfModel resumed = await self.ResumeAsync("paulo", Ct);

        // Resuming says "look again", not "everything is fine".
        Assert.Equal(OperationalState.Degraded, resumed.OperationalState);
        Assert.Null(resumed.PausedReason);
    }

    [Fact]
    public async Task EachReadingIsANewVersionRatherThanAnOverwrite()
    {
        using var db = new SqliteTestDb();
        SqliteSelfModel self = Build(db, new TestClock(At("2026-01-15T09:00:00+00:00")));

        SelfModel first = await self.RefreshAsync("local", Ct);
        SelfModel second = await self.RefreshAsync("local", Ct);

        Assert.Equal(first.Version + 1, second.Version);
        Assert.NotEqual(first.Id, second.Id);
    }
}
