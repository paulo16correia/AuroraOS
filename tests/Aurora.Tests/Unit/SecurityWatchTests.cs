using Aurora.Adapters.Incidents;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// The two security events Aurora declared and nobody raised (docs/adr/0064).
/// </summary>
/// <remarks>
/// Both exist because a refusal on its own is not enough: a request that is denied is handled, and
/// a request that is denied fifty times is somebody working at it. What is asserted here is where
/// the line is, that crossing it produces the right incident, and that a loop on the wrong side of
/// it does not produce four thousand.
/// </remarks>
public sealed class SecurityWatchTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");

    private static (SecurityWatch Watch, RecordingIncidentService Incidents, TestClock Clock) Build()
    {
        var clock = new TestClock(Now);
        var incidents = new RecordingIncidentService();
        return (new SecurityWatch(incidents, clock), incidents, clock);
    }

    [Fact]
    public async Task AMistypedCredentialIsNotAnIncident()
    {
        (SecurityWatch watch, RecordingIncidentService incidents, _) = Build();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await watch.AuthenticationFailedAsync("api", Ct);
        }

        // Four is somebody getting it wrong. An incident here would train whoever reads them to
        // stop reading them.
        Assert.Empty(incidents.Opened);
    }

    [Fact]
    public async Task FiveCredentialsInAWindowIs()
    {
        (SecurityWatch watch, RecordingIncidentService incidents, _) = Build();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await watch.AuthenticationFailedAsync("api", Ct);
        }

        SecurityEvent raised = Assert.Single(incidents.Opened);
        Assert.Equal(SecurityEventType.AuthenticationAbuse, raised.Type);
        Assert.Equal(SecuritySeverity.High, raised.Severity);
        Assert.Equal("api", raised.ActorRef);

        // Nothing named, so containment revokes standing consent and disables nothing: whoever is
        // guessing is not a component Aurora can switch off.
        Assert.Empty(raised.ResourceRef);

        // The evidence says how many and over what, and quotes nothing from the request. A record
        // holding a near-miss of the real token would be worse than no record.
        Assert.Contains("5 credentials refused", raised.EvidenceRef, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AttemptsSpreadPastTheWindowDoNotAccumulate()
    {
        (SecurityWatch watch, RecordingIncidentService incidents, TestClock clock) = Build();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await watch.AuthenticationFailedAsync("api", Ct);
        }

        clock.UtcNow = Now.AddMinutes(6);
        await watch.AuthenticationFailedAsync("api", Ct);

        // The window bounds how far apart attempts can be and still be one attack. Four yesterday
        // and one today is not five.
        Assert.Empty(incidents.Opened);
    }

    [Fact]
    public async Task TwoSurfacesAreCountedApart()
    {
        (SecurityWatch watch, RecordingIncidentService incidents, _) = Build();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await watch.AuthenticationFailedAsync("api", Ct);
            await watch.AuthenticationFailedAsync("mcp", Ct);
        }

        Assert.Empty(incidents.Opened);

        await watch.AuthenticationFailedAsync("mcp", Ct);
        Assert.Single(incidents.Opened);
    }

    [Fact]
    public async Task ALoopThatKeepsFailingOpensOneIncidentAndNotThousands()
    {
        (SecurityWatch watch, RecordingIncidentService incidents, _) = Build();

        for (var attempt = 0; attempt < 200; attempt++)
        {
            await watch.AuthenticationFailedAsync("api", Ct);
        }

        // An incident log with four thousand entries says less than one entry saying the same
        // thing happened four thousand times.
        Assert.Single(incidents.Opened);
    }

    [Fact]
    public async Task AnEscalationIsAnIncidentOnTheFirstAttempt()
    {
        (SecurityWatch watch, RecordingIncidentService incidents, _) = Build();

        await watch.PrivilegeEscalationAsync(
            "acme/notes", "plugin/acme/notes", "PERMISSION_NOT_GRANTED: never granted: notes.write", Ct);

        // No threshold. Asking for authority that was never granted does not happen by accident
        // the way a mistyped credential does.
        SecurityEvent raised = Assert.Single(incidents.Opened);
        Assert.Equal(SecurityEventType.PrivilegeEscalation, raised.Type);
        Assert.Equal(SecuritySeverity.High, raised.Severity);

        // Named, so containment disables this plugin and not every plugin.
        Assert.Equal("plugin/acme/notes", raised.ResourceRef);
        Assert.Contains("never granted", raised.EvidenceRef, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameEscalationRepeatingIsStillOneIncident()
    {
        (SecurityWatch watch, RecordingIncidentService incidents, TestClock clock) = Build();

        for (var attempt = 0; attempt < 50; attempt++)
        {
            await watch.PrivilegeEscalationAsync("acme/notes", "plugin/acme/notes", "again", Ct);
        }

        Assert.Single(incidents.Opened);

        // And past the window it is a new one, because something that started again an hour later
        // is news.
        clock.UtcNow = Now.AddMinutes(6);
        await watch.PrivilegeEscalationAsync("acme/notes", "plugin/acme/notes", "again", Ct);
        Assert.Equal(2, incidents.Opened.Count);
    }

    [Fact]
    public async Task TwoDifferentPluginsAreTwoIncidents()
    {
        (SecurityWatch watch, RecordingIncidentService incidents, _) = Build();

        await watch.PrivilegeEscalationAsync("acme/one", "plugin/acme/one", "x", Ct);
        await watch.PrivilegeEscalationAsync("acme/two", "plugin/acme/two", "x", Ct);

        // Deduplication is per actor and resource. Collapsing them would hide the second plugin.
        Assert.Equal(2, incidents.Opened.Count);
    }

    [Fact]
    public async Task AnIncidentServiceThatThrowsDoesNotBreakTheRefusalPath()
    {
        var watch = new SecurityWatch(new ThrowingIncidents(), new TestClock(Now));

        // The caller has already refused whatever this was about, and the refusal is what protects
        // the system. Failing to record it must not turn a handled refusal into a 500.
        await watch.PrivilegeEscalationAsync("acme/notes", "plugin/acme/notes", "x", Ct);
        await watch.PrivilegeEscalationAsync("acme/notes", "plugin/acme/notes", "x", Ct);
    }

    private sealed class ThrowingIncidents : Core.Abstractions.IIncidentService
    {
        public Task<Incident> OpenAsync(SecurityEvent securityEvent, CancellationToken ct) =>
            throw new IncidentException("the database is gone");

        public Task<Incident> ResolveAsync(
            string incidentId, string resolution, string actor, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Incident?> GetAsync(string incidentId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Incident>> OpenIncidentsAsync(CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
