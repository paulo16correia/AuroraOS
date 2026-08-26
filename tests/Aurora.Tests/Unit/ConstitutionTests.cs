using Aurora.Adapters.Constitution;
using Aurora.Core.Contracts;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// RFC 035: the eight Articles, applied rather than quoted.
/// </summary>
/// <remarks>
/// Until this existed the Constitution was eight paragraphs of prose and a version string on the
/// genome — the one part of the specification that could be contradicted without anything noticing.
/// </remarks>
public sealed class ConstitutionTests
{
    private static readonly ArticleConstitution Constitution = new();
    private const string At = "2026-01-01T00:00:00.0000000Z";

    private static Decision Decision(
        string mode = DecisionMode.ToolCall,
        double confidence = 0.9,
        IReadOnlyList<string>? uncertainty = null,
        IReadOnlyList<string>? evidence = null,
        IReadOnlyList<string>? policies = null,
        IReadOnlyList<string>? blocking = null,
        bool reversible = true,
        bool approvalRequired = true,
        string? expiry = At,
        string rationale = "it is what was asked for") => new(
        "d1", "cycle-1", mode, null,
        new DecisionOption(
            mode, rationale, ["says hello"],
            new OptionEvaluation(0.9, HasEvidence: true, "Low", 1, Permitted: true, Reversible: reversible),
            [], blocking ?? []),
        [], evidence ?? ["memory/1"], uncertainty ?? [], "Low", confidence,
        policies ?? ["policy/1"], approvalRequired, expiry, DecisionState.Committed);

    private static string ResultFor(Decision decision, string article) =>
        Constitution.Assess(decision, At).Conflicts
            .SingleOrDefault(c => c.Article == article)?.Result
        ?? ConstitutionalResult.Pass;

    [Fact]
    public void EveryArticleIsCheckedAndOnlyTheConflictsAreKept()
    {
        ConstitutionalAssessment assessment = Constitution.Assess(Decision(), At);

        // All eight named, so a later reader can tell an Article that passed from one nobody
        // thought to apply.
        Assert.Equal(8, assessment.ArticlesChecked.Count);
        Assert.Equal(ConstitutionalArticle.All, assessment.ArticlesChecked);

        // A list of eight passes is not a finding, and burying the one conflict among seven is how
        // a conflict stops being read.
        Assert.All(assessment.Conflicts, c => Assert.NotEqual(ConstitutionalResult.Pass, c.Result));
    }

    [Fact]
    public void ADecisionThatChangesNothingOutsideAuroraCanPassOutright()
    {
        ConstitutionalAssessment assessment = Constitution.Assess(
            Decision(mode: DecisionMode.Respond, approvalRequired: false, expiry: null), At);

        // Article 1 is a real verdict here rather than a shrug: a decision that reaches nothing
        // outside Aurora cannot disclose anything.
        Assert.Equal(ConstitutionalResult.Pass, assessment.Result);
        Assert.Empty(assessment.Conflicts);
    }

    [Fact]
    public void ActingOutsideAuroraIsNeverMoreThanReview()
    {
        ConstitutionalAssessment assessment = Constitution.Assess(Decision(), At);

        // Everything checkable passes, and what leaves Aurora still cannot be judged from
        // references. REVIEW does not block; it says a person should look.
        Assert.Equal(ConstitutionalResult.Review, assessment.Result);
        Assert.Equal(
            ConstitutionalResult.Review,
            ResultFor(Decision(), ConstitutionalArticle.DataProtection));
    }

    [Theory]
    // Article 2: middling confidence with nothing declared reads surer than it is.
    [InlineData(ConstitutionalArticle.TruthAndUncertainty)]
    // Article 3: irreversible and needing nobody's approval.
    [InlineData(ConstitutionalArticle.HumanControl)]
    // Article 4: a choice that states no reason.
    [InlineData(ConstitutionalArticle.TransparencyOfAction)]
    // Article 5: acting outside Aurora on nothing that can be cited.
    [InlineData(ConstitutionalArticle.ProvenanceOfLearning)]
    // Article 6: reaching outside with no policy decision behind it.
    [InlineData(ConstitutionalArticle.SecurityPrimacy)]
    // Article 7: the chosen option was blocked.
    [InlineData(ConstitutionalArticle.LeastPrivilege)]
    // Article 8: an effectful decision that never expires is a standing permission.
    [InlineData(ConstitutionalArticle.DecisionResponsibility)]
    public void EachArticleCatchesItsOwnContradiction(string article)
    {
        Decision offending = article switch
        {
            ConstitutionalArticle.TruthAndUncertainty => Decision(confidence: 0.5, uncertainty: []),
            ConstitutionalArticle.HumanControl => Decision(reversible: false, approvalRequired: false),
            ConstitutionalArticle.TransparencyOfAction => Decision(rationale: "   "),
            ConstitutionalArticle.ProvenanceOfLearning => Decision(evidence: []),
            ConstitutionalArticle.SecurityPrimacy => Decision(policies: []),
            ConstitutionalArticle.LeastPrivilege => Decision(blocking: ["not permitted"]),
            _ => Decision(expiry: null),
        };

        Assert.Equal(ConstitutionalResult.Fail, ResultFor(offending, article));
        Assert.Equal(ConstitutionalResult.Fail, Constitution.Assess(offending, At).Result);
    }

    [Fact]
    public void APolicyThatRelaxesALawIsRefusedAtPublication()
    {
        PolicyReviewReport report = Constitution.ReviewPolicy(
            new PolicyClaim("pv-2", ["files.write_anywhere"], [], RelaxesLaw: true, ["adr/9999"]), At);

        // Rule 1, and the only rule in RFC 035 with no middle answer. There is no reviewer to
        // escalate to and no context in which this becomes acceptable.
        Assert.False(report.Accepted);
        Assert.Equal(ConstitutionalResult.Fail, report.Assessment.Result);
        Assert.Contains("narrow", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void APolicyThatCitesNothingIsRefused()
    {
        PolicyReviewReport report = Constitution.ReviewPolicy(
            new PolicyClaim("pv-3", [], ["files.write_anywhere"], RelaxesLaw: false, []), At);

        // Article 8: a rule nobody can attribute is a rule nobody can revoke.
        Assert.False(report.Accepted);
        Assert.Contains("cites nothing", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void APolicyThatBothPermitsAndDeniesTheSameThingIsRefused()
    {
        PolicyReviewReport report = Constitution.ReviewPolicy(
            new PolicyClaim("pv-4", ["mail.send"], ["mail.send"], RelaxesLaw: false, ["adr/1"]), At);

        // Not so much unconstitutional as unenforceable: which list is consulted first would
        // decide what Aurora does.
        Assert.False(report.Accepted);
        Assert.Contains("both permits and denies", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void APolicyThatOnlyNarrowsIsPublished()
    {
        PolicyReviewReport report = Constitution.ReviewPolicy(
            new PolicyClaim("pv-5", [], ["files.write_anywhere"], RelaxesLaw: false, ["adr/1"]), At);

        Assert.True(report.Accepted);
        Assert.Equal(ConstitutionalResult.Pass, report.Assessment.Result);
    }

    [Fact]
    public void TheSameDecisionAssessedTwiceGivesTheSameAnswer()
    {
        Decision decision = Decision(blocking: ["not permitted"]);

        // Pure, so an assessment stored beside a decision can be re-derived and compared rather
        // than only trusted. Ids and timestamps differ; the verdict does not.
        Assert.Equal(
            Constitution.Assess(decision, At).Conflicts.Select(c => (c.Article, c.Result, c.Detail)),
            Constitution.Assess(decision, At).Conflicts.Select(c => (c.Article, c.Result, c.Detail)));
    }
}
