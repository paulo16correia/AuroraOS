namespace Aurora.Core.Contracts;

public static class CuriosityStatus
{
    public const string Candidate = "CANDIDATE";
    public const string Approved = "APPROVED";
    public const string Scheduled = "SCHEDULED";
    public const string Researching = "RESEARCHING";

    /// <summary>Investigated, and the result recorded by reference. Not the same as believed.</summary>
    public const string Learned = "LEARNED";

    public const string Rejected = "REJECTED";
    public const string Expired = "EXPIRED";
}

/// <summary>Why a proposal was refused. Closed set, so a refusal can be checked.</summary>
public static class CuriosityRefusal
{
    public const string SourceNotPermitted = "SOURCE_NOT_PERMITTED";
    public const string AboveSensitivityCeiling = "ABOVE_SENSITIVITY_CEILING";
    public const string ReachesOutside = "WOULD_REACH_OUTSIDE_AURORA";
    public const string NoResources = "NO_RESOURCES_TO_SPARE";
    public const string WrongMoment = "NOT_AN_APPROPRIATE_MOMENT";
    public const string OutrankedByNeeds = "OUTRANKED_BY_AN_OPEN_INCIDENT";
}

/// <summary>
/// A question Aurora would like answered, and the limits on answering it (RFC 032).
/// </summary>
/// <remarks>
/// A proposal, and only ever a proposal. There is deliberately no field here that could express
/// sending, buying, or reaching into an account: curiosity is a governed capacity for discovery,
/// not a licence to go and collect things.
/// </remarks>
public sealed record CuriosityProposal(
    string Id,
    string Question,
    IReadOnlyList<string> RationaleRefs,
    double ExpectedValue,
    string Scope,
    /// <summary>The only places this may be answered from. An allowlist, never a blocklist.</summary>
    IReadOnlyList<string> AllowedSources,
    string SensitivityLimit,
    double ResourceBudget,
    string Status,
    bool ApprovalRequired,
    IReadOnlyList<string> ResultRefs,
    string? ReviewAtUtc,
    string DetectedAtUtc,
    IReadOnlyList<string> RefusalReasons,
    string? GoalRef = null);

/// <summary>Something Aurora keeps running into and does not know.</summary>
/// <remarks>
/// Carries how often it came up and how sure Aurora is, because rule 3 makes curiosity compete for
/// resources against everything else and it needs a stated value to compete with.
/// </remarks>
public sealed record KnowledgeGap(
    string SubjectRef,
    string Question,
    int TimesSeen,
    double Confidence,
    IReadOnlyList<string> RationaleRefs,
    /// <summary>Where an answer would come from. Checked against the allowlist, not trusted.</summary>
    string Source,
    string SensitivityClass = Sensitivity.Public);

public sealed record CuriositySnapshot(IReadOnlyList<KnowledgeGap> Gaps);

/// <summary>
/// The limits inside which Aurora is allowed to be curious.
/// </summary>
/// <remarks>
/// Every field is a ceiling. There is no setting that widens what curiosity may reach — the policy
/// can only make it narrower, which is what "limited by rule" has to mean if it means anything.
/// </remarks>
public sealed record CuriosityPolicy(
    IReadOnlyList<string> AllowedSources,
    string SensitivityCeiling = Sensitivity.Public,
    double MaxBudgetPerProposal = 0.25,
    int MinTimesSeen = 3,
    double MaxConfidenceToAsk = 0.5,
    TimeSpan? ReviewAfter = null)
{
    public TimeSpan Review => ReviewAfter ?? TimeSpan.FromDays(14);

    /// <summary>
    /// The default is Aurora's own records and nothing else.
    /// </summary>
    /// <remarks>
    /// A deployment that wants Aurora reading anything further has to say so explicitly. Shipping
    /// an open default would make every later restriction a thing somebody had to remember.
    /// </remarks>
    public static CuriosityPolicy Default { get; } = new(["aurora/memory", "aurora/world"]);
}

public sealed class CuriosityException : Exception
{
    public CuriosityException(string message) : base(message)
    {
    }
}
