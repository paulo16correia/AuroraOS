using Aurora.Adapters.Capability;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>Conformance tests for RFC 051.</summary>
public sealed class CapabilityResolverTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static SqliteCapabilityResolver New(SqliteTestDb db) => new(db.Factory);

    private static CapabilityDefinition Communicate() => new(
        "communication.send", CapabilityDomain.Communication, """{"type":"object"}""",
        EffectClasses: ["message.send"], RiskClass: "MEDIUM",
        RequiredPermissions: ["comms.send"]);

    private static CapabilityProvider Provider(
        string id, int priority = 1, bool available = true, double cost = 1,
        IReadOnlyList<string>? effects = null, IReadOnlyList<string>? constraints = null,
        IReadOnlyList<string>? dataClasses = null) =>
        new(id, "communication.send", $"app/{id}", $"tool/{id}", priority, available, cost,
            dataClasses ?? ["PRIVATE"], constraints ?? [], effects ?? ["message.send"]);

    private static CapabilityRequest Request(
        string? pinned = null, string? preferred = null, params string[] constraints) =>
        new(Guid.NewGuid().ToString("N"), "decision/1", "communication.send",
            """{"body":"hello"}""", constraints, CapabilityRequestStatus.Requested, pinned, preferred);

    private static ResolutionContext Context(double ceiling = 10) =>
        new(["comms.send"], ceiling, ["PRIVATE", "PUBLIC"]);

    // ---- rule 1: the Mind asks for a capability, not a supplier ----

    [Fact]
    public async Task ACapabilityResolvesToWhicheverProviderIsPermitted()
    {
        using var db = new SqliteTestDb();
        var resolver = New(db);
        await resolver.RegisterCapabilityAsync(Communicate(), Ct);
        await resolver.RegisterProviderAsync(Provider("email", priority: 2), Ct);
        await resolver.RegisterProviderAsync(Provider("discord", priority: 1), Ct);

        CapabilityRequest resolved = await resolver.ResolveAsync(Request(), Context(), Ct);

        // The request never named a supplier; the Kernel picked one.
        Assert.Equal(CapabilityRequestStatus.Resolved, resolved.Status);
        Assert.Equal("discord", resolved.ResolvedProviderId);
    }

    // ---- rule 3: a provider cannot exceed the manifest ----

    [Fact]
    public async Task AProviderDeclaringEffectsBeyondTheManifestIsRefused()
    {
        using var db = new SqliteTestDb();
        var resolver = New(db);
        await resolver.RegisterCapabilityAsync(Communicate(), Ct);

        CapabilityResolutionException error = await Assert.ThrowsAsync<CapabilityResolutionException>(
            () => resolver.RegisterProviderAsync(
                Provider("rogue", effects: ["message.send", "files.delete"]), Ct));

        Assert.Contains("files.delete", error.Message, StringComparison.Ordinal);
    }

    // ---- rule 2: permissions, cost, classification, availability, preference ----

    [Fact]
    public async Task AMissingPermissionBlocksEverything()
    {
        using var db = new SqliteTestDb();
        var resolver = New(db);
        await resolver.RegisterCapabilityAsync(Communicate(), Ct);
        await resolver.RegisterProviderAsync(Provider("email"), Ct);

        CapabilityRequest resolved = await resolver.ResolveAsync(
            Request(), new ResolutionContext([], 10, ["PRIVATE"]), Ct);

        Assert.Equal(CapabilityRequestStatus.Blocked, resolved.Status);
    }

    [Fact]
    public async Task APreferenceOrdersButDoesNotOverrideTheCostCeiling()
    {
        using var db = new SqliteTestDb();
        var resolver = New(db);
        await resolver.RegisterCapabilityAsync(Communicate(), Ct);
        await resolver.RegisterProviderAsync(Provider("cheap", priority: 5, cost: 1), Ct);
        await resolver.RegisterProviderAsync(Provider("preferred", priority: 1, cost: 50), Ct);

        CapabilityRequest resolved = await resolver.ResolveAsync(
            Request(preferred: "preferred"), Context(ceiling: 10), Ct);

        // The preference is real, but it does not buy its way past a limit.
        Assert.Equal("cheap", resolved.ResolvedProviderId);
    }

    [Fact]
    public async Task APreferenceWinsAmongOtherwiseEligibleProviders()
    {
        using var db = new SqliteTestDb();
        var resolver = New(db);
        await resolver.RegisterCapabilityAsync(Communicate(), Ct);
        await resolver.RegisterProviderAsync(Provider("first", priority: 1), Ct);
        await resolver.RegisterProviderAsync(Provider("preferred", priority: 9), Ct);

        CapabilityRequest resolved = await resolver.ResolveAsync(
            Request(preferred: "preferred"), Context(), Ct);

        Assert.Equal("preferred", resolved.ResolvedProviderId);
    }

    [Fact]
    public async Task AnUnmetTargetConstraintExcludesAProvider()
    {
        using var db = new SqliteTestDb();
        var resolver = New(db);
        await resolver.RegisterCapabilityAsync(Communicate(), Ct);
        await resolver.RegisterProviderAsync(Provider("plain"), Ct);
        await resolver.RegisterProviderAsync(
            Provider("encrypted", priority: 9, constraints: ["end_to_end"]), Ct);

        CapabilityRequest resolved = await resolver.ResolveAsync(
            Request(constraints: "end_to_end"), Context(), Ct);

        Assert.Equal("encrypted", resolved.ResolvedProviderId);
    }

    [Fact]
    public async Task AProviderHandlingForbiddenDataClassesIsExcluded()
    {
        using var db = new SqliteTestDb();
        var resolver = New(db);
        await resolver.RegisterCapabilityAsync(Communicate(), Ct);
        await resolver.RegisterProviderAsync(Provider("leaky", dataClasses: ["SECRET"]), Ct);

        CapabilityRequest resolved = await resolver.ResolveAsync(Request(), Context(), Ct);

        Assert.Equal(CapabilityRequestStatus.Blocked, resolved.Status);
    }

    // ---- limit case: no provider means blocked, never a generic shell ----

    [Fact]
    public async Task NoProviderBlocksAndNamesTheMissingCapability()
    {
        using var db = new SqliteTestDb();
        var resolver = New(db);
        await resolver.RegisterCapabilityAsync(Communicate(), Ct);

        CapabilityRequest resolved = await resolver.ResolveAsync(Request(), Context(), Ct);

        Assert.Equal(CapabilityRequestStatus.Blocked, resolved.Status);
        Assert.Contains("communication.send", resolved.BlockedReason!, StringComparison.Ordinal);
        Assert.Null(resolved.ResolvedProviderId);
    }

    // ---- rule 4 and its limit case: no silent substitution ----

    [Fact]
    public async Task APinnedProviderIsNeverSubstituted()
    {
        using var db = new SqliteTestDb();
        var resolver = New(db);
        await resolver.RegisterCapabilityAsync(Communicate(), Ct);
        await resolver.RegisterProviderAsync(Provider("email", available: false), Ct);
        await resolver.RegisterProviderAsync(Provider("discord"), Ct);

        // "Send this by email to that person" is the intention, not "communicate somehow".
        CapabilityRequest resolved = await resolver.ResolveAsync(Request(pinned: "email"), Context(), Ct);

        Assert.Equal(CapabilityRequestStatus.Blocked, resolved.Status);
        Assert.Null(resolved.ResolvedProviderId);
    }

    [Fact]
    public async Task AFailedPinnedProviderBlocksRatherThanFallingBack()
    {
        using var db = new SqliteTestDb();
        var resolver = New(db);
        await resolver.RegisterCapabilityAsync(Communicate(), Ct);
        await resolver.RegisterProviderAsync(Provider("email"), Ct);
        await resolver.RegisterProviderAsync(Provider("discord", priority: 9), Ct);
        CapabilityRequest resolved = await resolver.ResolveAsync(Request(pinned: "email"), Context(), Ct);
        Assert.Equal("email", resolved.ResolvedProviderId);

        CapabilityRequest afterFailure = await resolver.HandleProviderFailureAsync(
            resolved.Id, "smtp refused the connection", Context(), Ct);

        Assert.Equal(CapabilityRequestStatus.Blocked, afterFailure.Status);
        Assert.Contains("named a specific provider", afterFailure.BlockedReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnpinnedFailureMayFallBackToAnEquallyPermittedProvider()
    {
        using var db = new SqliteTestDb();
        var resolver = New(db);
        await resolver.RegisterCapabilityAsync(Communicate(), Ct);
        await resolver.RegisterProviderAsync(Provider("email", priority: 1), Ct);
        await resolver.RegisterProviderAsync(Provider("discord", priority: 2), Ct);
        CapabilityRequest resolved = await resolver.ResolveAsync(Request(), Context(), Ct);
        Assert.Equal("email", resolved.ResolvedProviderId);

        await resolver.RegisterProviderAsync(Provider("email", priority: 1, available: false), Ct);
        CapabilityRequest afterFailure = await resolver.HandleProviderFailureAsync(
            resolved.Id, "smtp refused the connection", Context(), Ct);

        // The intention was to communicate, nothing narrower, so an equally permitted route is fine.
        Assert.Equal(CapabilityRequestStatus.Resolved, afterFailure.Status);
        Assert.Equal("discord", afterFailure.ResolvedProviderId);
    }

    [Fact]
    public async Task AFailureWithNoAlternativeBlocks()
    {
        using var db = new SqliteTestDb();
        var resolver = New(db);
        await resolver.RegisterCapabilityAsync(Communicate(), Ct);
        await resolver.RegisterProviderAsync(Provider("email"), Ct);
        CapabilityRequest resolved = await resolver.ResolveAsync(Request(), Context(), Ct);

        CapabilityRequest afterFailure = await resolver.HandleProviderFailureAsync(
            resolved.Id, "smtp refused the connection", Context(), Ct);

        Assert.Equal(CapabilityRequestStatus.Blocked, afterFailure.Status);
    }

    // ---- the resolution is explainable ----

    [Fact]
    public async Task EveryProviderGetsAVerdictInTheReport()
    {
        using var db = new SqliteTestDb();
        var resolver = New(db);
        await resolver.RegisterCapabilityAsync(Communicate(), Ct);
        await resolver.RegisterProviderAsync(Provider("email", priority: 1), Ct);
        await resolver.RegisterProviderAsync(Provider("discord", priority: 2), Ct);
        await resolver.RegisterProviderAsync(Provider("sms", available: false), Ct);

        CapabilityRequest resolved = await resolver.ResolveAsync(Request(), Context(), Ct);
        ResolutionReport report = await resolver.ExplainResolutionAsync(resolved.Id, Ct);

        Assert.Equal(3, report.Verdicts.Count);
        Assert.Equal("email", report.ChosenProviderId);
        Assert.Equal(
            ResolutionReason.Unavailable,
            report.Verdicts.Single(v => v.ProviderId == "sms").Reason);
        Assert.Equal(
            ResolutionReason.LowerPriority,
            report.Verdicts.Single(v => v.ProviderId == "discord").Reason);
    }

    [Fact]
    public async Task ABlockedResolutionExplainsItself()
    {
        using var db = new SqliteTestDb();
        var resolver = New(db);
        await resolver.RegisterCapabilityAsync(Communicate(), Ct);
        await resolver.RegisterProviderAsync(Provider("email", available: false), Ct);

        CapabilityRequest resolved = await resolver.ResolveAsync(Request(), Context(), Ct);
        ResolutionReport report = await resolver.ExplainResolutionAsync(resolved.Id, Ct);

        Assert.Null(report.ChosenProviderId);
        Assert.Contains("not resolved", report.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolvingAnUnknownCapabilityIsRefused()
    {
        using var db = new SqliteTestDb();

        await Assert.ThrowsAsync<CapabilityResolutionException>(() => New(db).ResolveAsync(
            Request() with { CapabilityId = "shell.execute_anything" }, Context(), Ct));
    }
}
