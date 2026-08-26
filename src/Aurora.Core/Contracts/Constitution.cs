namespace Aurora.Core.Contracts;

/// <summary>
/// The eight Articles (RFC 035), named once so a check cannot cite one that does not exist.
/// </summary>
public static class ConstitutionalArticle
{
    public const string DataProtection = "ART_1_DATA_PROTECTION";
    public const string TruthAndUncertainty = "ART_2_TRUTH_AND_UNCERTAINTY";
    public const string HumanControl = "ART_3_HUMAN_CONTROL";
    public const string TransparencyOfAction = "ART_4_TRANSPARENCY_OF_ACTION";
    public const string ProvenanceOfLearning = "ART_5_PROVENANCE_OF_LEARNING";
    public const string SecurityPrimacy = "ART_6_SECURITY_PRIMACY";
    public const string LeastPrivilege = "ART_7_LEAST_PRIVILEGE";
    public const string DecisionResponsibility = "ART_8_DECISION_RESPONSIBILITY";

    /// <summary>All eight, in the order the RFC states them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        DataProtection, TruthAndUncertainty, HumanControl, TransparencyOfAction,
        ProvenanceOfLearning, SecurityPrimacy, LeastPrivilege, DecisionResponsibility,
    ];
}

public static class ConstitutionalResult
{
    /// <summary>Every Article that could be checked was checked, and none conflicts.</summary>
    public const string Pass = "PASS";

    /// <summary>At least one Article is contradicted. The subject does not proceed.</summary>
    public const string Fail = "FAIL";

    /// <summary>
    /// An Article that bears on this could not be judged from what Aurora holds.
    /// </summary>
    /// <remarks>
    /// The same discipline as an evaluation verdict (docs/adr/0055): a constitution that returned
    /// PASS for what it did not look at would turn the assessment into a rubber stamp, and the
    /// stamp would then be cited as the reason the decision was safe.
    /// </remarks>
    public const string Review = "REVIEW";
}

/// <summary>One Article's verdict on one subject, and why.</summary>
public sealed record ArticleFinding(string Article, string Result, string Detail);

/// <summary>
/// What the Constitution concluded about something, kept so the conclusion can be read back
/// against the decision it justified (RFC 035).
/// </summary>
public sealed record ConstitutionalAssessment(
    string Id,
    string SubjectRef,
    IReadOnlyList<string> ArticlesChecked,
    string Result,
    IReadOnlyList<ArticleFinding> Conflicts,
    IReadOnlyList<string> EvidenceRefs,
    string AssessedAtUtc);

/// <summary>
/// What a policy version claims it will permit, declared before it is published.
/// </summary>
/// <param name="RelaxesLaw">
/// Whether this policy would loosen a Law rather than a preference. A policy may narrow anything
/// and may widen nothing that a Law fixed — RFC 035 rule 1 is the only rule here with no middle
/// answer.
/// </param>
public sealed record PolicyClaim(
    string PolicyVersion,
    IReadOnlyList<string> Permits,
    IReadOnlyList<string> Denies,
    bool RelaxesLaw,
    IReadOnlyList<string> EvidenceRefs);

/// <summary>Whether a policy version may be published, and what stopped it if not.</summary>
public sealed record PolicyReviewReport(
    string PolicyVersion,
    bool Accepted,
    ConstitutionalAssessment Assessment,
    string Reason);

public sealed class ConstitutionException : Exception
{
    public ConstitutionException(string message) : base(message)
    {
    }
}
