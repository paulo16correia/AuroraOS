using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// The Articles, applied rather than quoted (RFC 035).
/// </summary>
/// <remarks>
/// RFC 035 was, until this existed, eight paragraphs of prose and a version string on the genome.
/// Nothing checked a policy against it and no decision carried a reference to it, which made the
/// Constitution the one part of the specification that could be contradicted without anything
/// noticing.
/// <para>
/// What it can check, it checks against facts Aurora already holds — a decision's evidence, its
/// policy decisions, its expiry, whether approval was required for something irreversible. What it
/// cannot judge locally it returns as <see cref="ConstitutionalResult.Review"/>, never as a pass.
/// </para>
/// </remarks>
public interface IConstitution
{
    /// <summary>
    /// Judges a decision against every Article that bears on it.
    /// </summary>
    /// <remarks>
    /// Pure: the same decision assessed twice gives the same answer, so an assessment stored
    /// alongside a decision can be re-derived and compared rather than trusted.
    /// </remarks>
    ConstitutionalAssessment Assess(Decision decision, string assessedAtUtc);

    /// <summary>
    /// Decides whether a policy version may be published (RFC 035 rule 1).
    /// </summary>
    PolicyReviewReport ReviewPolicy(PolicyClaim claim, string assessedAtUtc);
}
