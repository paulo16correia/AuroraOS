using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>Describes the installation a genome is being resolved for.</summary>
public sealed record InstallationContext(
    string InstallationId,
    IReadOnlyList<string> SelectedVariants,
    IReadOnlyList<GenomeOverride> Overrides,
    IReadOnlyList<string> AvailableCapabilityIds);

/// <summary>Signs and verifies a genome manifest (RFC 036 rule 1).</summary>
public interface IGenomeSigner
{
    string Sign(Genome unsigned);

    bool Verify(Genome genome);
}

/// <summary>Resolution and bootstrap of genomes (RFC 036).</summary>
public interface IGenomeService
{
    Task<Genome> RegisterAsync(Genome genome, CancellationToken ct);

    Task<Genome?> GetAsync(string genomeId, CancellationToken ct);

    /// <summary>
    /// Resolves a genome for an installation. Refuses a genome whose signature does not verify —
    /// RFC 036 is explicit that an invalid signature means no instance is created.
    /// </summary>
    Task<GenomeResolution> ResolveAsync(
        string genomeId, InstallationContext context, CancellationToken ct);

    /// <summary>
    /// Judges one override. A variant may restrict capabilities or policies and may never relax
    /// the Constitution, the Laws or a security guarantee.
    /// </summary>
    OverrideDecision ValidateOverride(Genome genome, GenomeOverride change);

    /// <summary>Turns a resolution into a bootstrap plan, degrading rather than substituting.</summary>
    Task<BootstrapPlan> BootstrapAsync(string resolutionId, CancellationToken ct);

    Task<GenomeResolution?> GetResolutionAsync(string resolutionId, CancellationToken ct);
}
