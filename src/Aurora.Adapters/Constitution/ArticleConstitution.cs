using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Constitution;

/// <summary>
/// Applies the eight Articles to a decision, and to a policy before it is published (RFC 035).
/// </summary>
/// <remarks>
/// Every check reads something the decision already carries. Nothing here asks a model whether a
/// decision feels constitutional, and nothing invents a number: an Article that cannot be judged
/// from the decision comes back as <see cref="ConstitutionalResult.Review"/> and drags the whole
/// assessment there with it.
/// <para>
/// That makes REVIEW the common answer for anything touching data, which is correct and is the
/// point. Article 1 is about what a piece of information is, and Aurora holds a decision's
/// references rather than its contents; claiming to have cleared it would be the assessment
/// lying about its own reach.
/// </para>
/// </remarks>
public sealed class ArticleConstitution : IConstitution
{
    public ConstitutionalAssessment Assess(Decision decision, string assessedAtUtc)
    {
        List<ArticleFinding> findings =
        [
            DataProtection(decision),
            TruthAndUncertainty(decision),
            HumanControl(decision),
            TransparencyOfAction(decision),
            ProvenanceOfLearning(decision),
            SecurityPrimacy(decision),
            LeastPrivilege(decision),
            DecisionResponsibility(decision),
        ];

        var result = findings.Any(f => f.Result == ConstitutionalResult.Fail)
            ? ConstitutionalResult.Fail
            : findings.Any(f => f.Result == ConstitutionalResult.Review)
                ? ConstitutionalResult.Review
                : ConstitutionalResult.Pass;

        return new ConstitutionalAssessment(
            Guid.NewGuid().ToString("N"),
            $"decision/{decision.Id}",
            ConstitutionalArticle.All,
            result,

            // Only what went wrong. A list of eight passes is not a finding, and burying the one
            // conflict among seven is how a conflict stops being read.
            findings.Where(f => f.Result != ConstitutionalResult.Pass).ToList(),
            decision.EvidenceRefs,
            assessedAtUtc);
    }

    public PolicyReviewReport ReviewPolicy(PolicyClaim claim, string assessedAtUtc)
    {
        // Rule 1, and the only rule in RFC 035 with no middle answer: a policy may narrow anything
        // and may widen nothing a Law fixed. There is no reviewer to escalate to and no context in
        // which this becomes acceptable, so it is refused at publication rather than at use.
        var conflicts = new List<ArticleFinding>();

        if (claim.RelaxesLaw)
        {
            conflicts.Add(new ArticleFinding(
                ConstitutionalArticle.SecurityPrimacy, ConstitutionalResult.Fail,
                "the policy relaxes a Law; policies narrow, they do not widen"));
        }

        if (claim.EvidenceRefs.Count == 0)
        {
            // Article 8: a change of this size is attributable or it is not published.
            conflicts.Add(new ArticleFinding(
                ConstitutionalArticle.DecisionResponsibility, ConstitutionalResult.Fail,
                "the policy cites nothing; a rule nobody can attribute is a rule nobody can revoke"));
        }

        foreach (var permitted in claim.Permits.Where(
            p => claim.Denies.Contains(p, StringComparer.Ordinal)))
        {
            // Not a constitutional matter so much as an unreadable one: a policy that both permits
            // and denies the same thing has no meaning to enforce, and the enforcement would then
            // depend on which list was consulted first.
            conflicts.Add(new ArticleFinding(
                ConstitutionalArticle.TransparencyOfAction, ConstitutionalResult.Fail,
                $"the policy both permits and denies {permitted}"));
        }

        var assessment = new ConstitutionalAssessment(
            Guid.NewGuid().ToString("N"),
            $"policy/{claim.PolicyVersion}",
            [ConstitutionalArticle.SecurityPrimacy, ConstitutionalArticle.TransparencyOfAction,
             ConstitutionalArticle.DecisionResponsibility],
            conflicts.Count == 0 ? ConstitutionalResult.Pass : ConstitutionalResult.Fail,
            conflicts,
            claim.EvidenceRefs,
            assessedAtUtc);

        return new PolicyReviewReport(
            claim.PolicyVersion,
            conflicts.Count == 0,
            assessment,
            conflicts.Count == 0
                ? "no Article is contradicted"
                : string.Join("; ", conflicts.Select(c => c.Detail)));
    }

    /// <summary>
    /// Article 1 — data protection.
    /// </summary>
    /// <remarks>
    /// A decision that reaches nothing outside Aurora cannot disclose anything, and that is a real
    /// verdict rather than a courtesy. One that does reach outside carries references, not
    /// contents, so what would travel cannot be judged from here: REVIEW, which does not block and
    /// does say a person should look.
    /// </remarks>
    private static ArticleFinding DataProtection(Decision decision) =>
        DecisionMode.HasExternalEffect(decision.Mode)
            ? new(ConstitutionalArticle.DataProtection, ConstitutionalResult.Review,
                "what would leave Aurora cannot be judged from references alone")
            : new(ConstitutionalArticle.DataProtection, ConstitutionalResult.Pass,
                "nothing leaves Aurora");

    /// <summary>Article 2 — Aurora declares material uncertainty rather than sounding sure.</summary>
    private static ArticleFinding TruthAndUncertainty(Decision decision) =>
        decision is { Confidence: < 0.7, Uncertainty.Count: 0 }
            ? new(ConstitutionalArticle.TruthAndUncertainty, ConstitutionalResult.Fail,
                $"confidence {decision.Confidence:F2} with nothing declared uncertain")
            : new(ConstitutionalArticle.TruthAndUncertainty, ConstitutionalResult.Pass,
                "uncertainty is declared or confidence does not require it");

    /// <summary>Article 3 — the person keeps control of anything that cannot be taken back.</summary>
    private static ArticleFinding HumanControl(Decision decision) =>
        !decision.SelectedOption.Evaluation.Reversible && !decision.ApprovalRequired
            ? new(ConstitutionalArticle.HumanControl, ConstitutionalResult.Fail,
                "irreversible and needing nobody's approval")
            : new(ConstitutionalArticle.HumanControl, ConstitutionalResult.Pass,
                "reversible, or the person decides");

    /// <summary>Article 4 — nothing done is hidden.</summary>
    private static ArticleFinding TransparencyOfAction(Decision decision) =>
        string.IsNullOrWhiteSpace(decision.SelectedOption.RationaleSummary)
            ? new(ConstitutionalArticle.TransparencyOfAction, ConstitutionalResult.Fail,
                "the chosen option carries no rationale")
            : new(ConstitutionalArticle.TransparencyOfAction, ConstitutionalResult.Pass,
                "the choice states its reason");

    /// <summary>
    /// Article 5 — everything Aurora acts on has an origin.
    /// </summary>
    /// <remarks>
    /// Required of a decision with an effect. Asking a question or staying silent needs no evidence
    /// behind it, and demanding some would push the engine to manufacture a citation for the act of
    /// admitting it does not know.
    /// </remarks>
    private static ArticleFinding ProvenanceOfLearning(Decision decision) =>
        DecisionMode.HasExternalEffect(decision.Mode) && decision.EvidenceRefs.Count == 0
            ? new(ConstitutionalArticle.ProvenanceOfLearning, ConstitutionalResult.Fail,
                "acting outside Aurora on nothing that can be cited")
            : new(ConstitutionalArticle.ProvenanceOfLearning, ConstitutionalResult.Pass,
                $"{decision.EvidenceRefs.Count} evidence reference(s), or nothing acted on");

    /// <summary>Article 6 — nothing overrides security, and policy is not optional.</summary>
    private static ArticleFinding SecurityPrimacy(Decision decision) =>
        DecisionMode.HasExternalEffect(decision.Mode) && decision.PolicyDecisionIds.Count == 0
            ? new(ConstitutionalArticle.SecurityPrimacy, ConstitutionalResult.Fail,
                $"{decision.Mode} reaches outside Aurora with no policy decision behind it")
            : new(ConstitutionalArticle.SecurityPrimacy, ConstitutionalResult.Pass,
                "policy was consulted, or nothing reaches outside");

    /// <summary>Article 7 — an option that was blocked does not become the chosen one.</summary>
    private static ArticleFinding LeastPrivilege(Decision decision) =>
        decision.SelectedOption.BlockingReasons.Count > 0
            ? new(ConstitutionalArticle.LeastPrivilege, ConstitutionalResult.Fail,
                $"the chosen option is blocked: {string.Join("; ", decision.SelectedOption.BlockingReasons)}")
            : new(ConstitutionalArticle.LeastPrivilege, ConstitutionalResult.Pass,
                "the chosen option carries no blocking reason");

    /// <summary>Article 8 — a decision with an effect is time-limited, or it is a standing permission.</summary>
    private static ArticleFinding DecisionResponsibility(Decision decision) =>
        DecisionMode.HasExternalEffect(decision.Mode) && decision.ExpiryAtUtc is null
            ? new(ConstitutionalArticle.DecisionResponsibility, ConstitutionalResult.Fail,
                "an effectful decision with no expiry is a standing permission")
            : new(ConstitutionalArticle.DecisionResponsibility, ConstitutionalResult.Pass,
                "bounded in time, or it changes nothing outside");
}
