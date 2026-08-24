using Aurora.Adapters.Genomes;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>Conformance tests for RFC 036.</summary>
public sealed class GenomeTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly EcdsaGenomeSigner _signer =
        new(System.Security.Cryptography.ECDsa.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256));

    public void Dispose() => _signer.Dispose();

    private static Genome Draft(string status = GenomeStatus.Released, params string[] capabilities) => new(
        "genome-1", "Aurora Personal", "1.0.0", null, status,
        "constitution-1", "laws-1", "identity/base", "personality/base", "development/base",
        MindSchemaVersion: 1,
        AllowedCapabilityIds: capabilities.Length == 0 ? ["clock.now", "echo.say"] : capabilities,
        PolicyBundleRefs: ["policy/base"],
        DefaultLocales: ["pt-PT"],
        BootstrapConfigurationRef: "bootstrap/base",
        IntegrityHash: string.Empty,
        Signature: string.Empty);

    private SqliteGenomeService Service(SqliteTestDb db, ICapabilityRegistry? registry = null) =>
        new(db.Factory, _signer, registry ?? new FakeRegistry(), new TestClock(DateTimeOffset.UnixEpoch));

    private static InstallationContext Context(params GenomeOverride[] overrides) =>
        new("install-1", ["personal"], overrides, []);

    // ---- rule 1: signed, versioned, reproducible ----

    [Fact]
    public void SealingProducesAVerifiableGenome()
    {
        Genome sealedGenome = _signer.Seal(Draft());

        Assert.True(_signer.Verify(sealedGenome));
        Assert.False(string.IsNullOrWhiteSpace(sealedGenome.IntegrityHash));
        Assert.False(string.IsNullOrWhiteSpace(sealedGenome.Signature));
    }

    [Fact]
    public void TamperingWithAFieldBreaksVerification()
    {
        Genome sealedGenome = _signer.Seal(Draft());

        // Widening the capability list without re-signing must not verify.
        Genome tampered = sealedGenome with { AllowedCapabilityIds = ["clock.now", "echo.say", "files.write_sandbox"] };

        Assert.False(_signer.Verify(tampered));
    }

    [Fact]
    public void AForeignSignatureDoesNotVerify()
    {
        Genome sealedGenome = _signer.Seal(Draft());

        using var other = new EcdsaGenomeSigner(System.Security.Cryptography.ECDsa.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256));

        Assert.False(other.Verify(sealedGenome));
    }

    [Fact]
    public async Task AnUnsignedGenomeIsNotRegistered()
    {
        using var db = new SqliteTestDb();

        await Assert.ThrowsAsync<GenomeException>(() => Service(db).RegisterAsync(Draft(), Ct));
    }

    [Fact]
    public async Task AnInvalidSignatureMeansNoInstanceIsCreated()
    {
        using var db = new SqliteTestDb();
        var service = Service(db);
        await service.RegisterAsync(_signer.Seal(Draft()), Ct);

        // Someone edits the stored row afterwards.
        using (var connection = db.Factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE genome SET allowed_capability_ids = 'clock.now,echo.say,vault.read';";
            command.ExecuteNonQuery();
        }

        await Assert.ThrowsAsync<GenomeException>(() => service.ResolveAsync("genome-1", Context(), Ct));
    }

    // ---- overrides may restrict, never relax ----

    [Theory]
    [InlineData("constitution_version")]
    [InlineData("law_set_version")]
    [InlineData("mind_schema_version")]
    public void AnOverrideOnAFixedFieldIsDenied(string field)
    {
        OverrideDecision decision = Service(new SqliteTestDb()).ValidateOverride(
            _signer.Seal(Draft()), new GenomeOverride(field, ["2"]));

        Assert.Equal(OverrideVerdict.Deny, decision.Verdict);
    }

    [Fact]
    public void RestrictingCapabilitiesIsAllowed()
    {
        using var db = new SqliteTestDb();

        OverrideDecision decision = Service(db).ValidateOverride(
            _signer.Seal(Draft()), new GenomeOverride("allowed_capability_ids", ["clock.now"]));

        Assert.Equal(OverrideVerdict.Allow, decision.Verdict);
    }

    [Fact]
    public void GrantingACapabilityTheGenomeDoesNotCarryIsDenied()
    {
        using var db = new SqliteTestDb();

        OverrideDecision decision = Service(db).ValidateOverride(
            _signer.Seal(Draft()), new GenomeOverride("allowed_capability_ids", ["clock.now", "vault.read"]));

        Assert.Equal(OverrideVerdict.Deny, decision.Verdict);
        Assert.Contains("vault.read", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovingAPolicyBundleIsDenied()
    {
        using var db = new SqliteTestDb();

        OverrideDecision decision = Service(db).ValidateOverride(
            _signer.Seal(Draft()), new GenomeOverride("policy_bundle_refs", []));

        Assert.Equal(OverrideVerdict.Deny, decision.Verdict);
    }

    [Fact]
    public void AnUnrecognisedFieldGoesToReview_NotToAGuess()
    {
        using var db = new SqliteTestDb();

        OverrideDecision decision = Service(db).ValidateOverride(
            _signer.Seal(Draft()), new GenomeOverride("something_new", ["x"]));

        Assert.Equal(OverrideVerdict.Review, decision.Verdict);
    }

    // ---- resolution ----

    [Fact]
    public async Task ResolutionAppliesRestrictionsAndRecordsRefusals()
    {
        using var db = new SqliteTestDb();
        var service = Service(db);
        await service.RegisterAsync(_signer.Seal(Draft()), Ct);

        GenomeResolution resolution = await service.ResolveAsync(
            "genome-1",
            Context(
                new GenomeOverride("allowed_capability_ids", ["clock.now"]),
                new GenomeOverride("law_set_version", ["laws-0"])),
            Ct);

        Assert.Equal(["clock.now"], resolution.EffectiveCapabilityIds);
        Assert.Single(resolution.DeniedOverrides);
        Assert.Contains("law_set_version", resolution.DeniedOverrides[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyAReleasedGenomeBirthsAnInstance()
    {
        using var db = new SqliteTestDb();
        var service = Service(db);
        await service.RegisterAsync(_signer.Seal(Draft(GenomeStatus.Draft)), Ct);

        await Assert.ThrowsAsync<GenomeException>(() => service.ResolveAsync("genome-1", Context(), Ct));
    }

    [Fact]
    public async Task TheSameInputsProduceTheSameEffectiveHash()
    {
        using var db = new SqliteTestDb();
        var service = Service(db);
        await service.RegisterAsync(_signer.Seal(Draft()), Ct);

        GenomeResolution first = await service.ResolveAsync("genome-1", Context(), Ct);
        GenomeResolution second = await service.ResolveAsync("genome-1", Context(), Ct);

        // Rule 1 calls the genome reproducible; a resolution of the same inputs must be too.
        Assert.Equal(first.EffectiveHash, second.EffectiveHash);
        Assert.NotEqual(first.Id, second.Id);
    }

    // ---- bootstrap degrades, never substitutes ----

    [Fact]
    public async Task BootstrapIsReadyWhenEveryCapabilityExists()
    {
        using var db = new SqliteTestDb();
        var registry = new FakeRegistry(
            new FakeCapability(FakeCapability.LowReadOnly("clock.now", """{"type":"object"}"""), _ => default),
            new FakeCapability(FakeCapability.LowReadOnly("echo.say", """{"type":"object"}"""), _ => default));
        var service = Service(db, registry);
        await service.RegisterAsync(_signer.Seal(Draft()), Ct);
        GenomeResolution resolution = await service.ResolveAsync("genome-1", Context(), Ct);

        BootstrapPlan plan = await service.BootstrapAsync(resolution.Id, Ct);

        Assert.Equal(BootstrapStatus.Ready, plan.Status);
        Assert.Empty(plan.MissingCapabilityIds);
    }

    [Fact]
    public async Task AMissingCapabilityDegradesTheBootstrap()
    {
        using var db = new SqliteTestDb();
        var registry = new FakeRegistry(
            new FakeCapability(FakeCapability.LowReadOnly("clock.now", """{"type":"object"}"""), _ => default));
        var service = Service(db, registry);
        await service.RegisterAsync(_signer.Seal(Draft()), Ct);
        GenomeResolution resolution = await service.ResolveAsync("genome-1", Context(), Ct);

        BootstrapPlan plan = await service.BootstrapAsync(resolution.Id, Ct);

        // Degraded, and the missing one is named. No substitute is invented.
        Assert.Equal(BootstrapStatus.Degraded, plan.Status);
        Assert.Equal(["echo.say"], plan.MissingCapabilityIds);
        Assert.DoesNotContain(plan.AvailableCapabilityIds, c => c == "echo.say");
    }

    [Fact]
    public async Task NoCapabilitiesAtAllBlocksTheBootstrap()
    {
        using var db = new SqliteTestDb();
        var service = Service(db);
        await service.RegisterAsync(_signer.Seal(Draft()), Ct);
        GenomeResolution resolution = await service.ResolveAsync("genome-1", Context(), Ct);

        BootstrapPlan plan = await service.BootstrapAsync(resolution.Id, Ct);

        Assert.Equal(BootstrapStatus.Blocked, plan.Status);
    }
}
