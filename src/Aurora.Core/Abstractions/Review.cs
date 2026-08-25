using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

public sealed record ReviewRequest(
    Principal Principal, string Timezone, long AfterAuditSequence = 0, int Limit = 100);

/// <summary>What the review found, all of it read from Aurora's own records.</summary>
public sealed record ReviewFindings(
    long HighestAuditSequence,
    int AuditEntries,
    IReadOnlyList<string> OpenNeeds,
    IReadOnlyList<string> PendingSignals,
    IReadOnlyList<string> GoalsPastReview,
    IReadOnlyList<string> OpenQuestions,
    IReadOnlyList<string> FailedSchedules,
    string RiskPosture,
    string ResourceStatus);

public sealed record ReviewOutcome(
    string CycleId,
    string DecisionId,
    string ActionId,
    string ObservationId,
    string ReflectionId,
    string Summary,
    IReadOnlyList<string> AuditRefs,
    IReadOnlyList<string> StagesRun,
    IReadOnlyList<string> StagesOmitted,
    ReviewFindings Findings);

/// <summary>
/// The second application: a review of what Aurora did and what is waiting (RFC 100 step 12).
/// </summary>
/// <remarks>
/// Low-risk and reading-only, which is what the frozen order permits at this point: it touches no
/// external tool and every source it reads is Aurora's own. It exists because the rest of the
/// system is only as governed as it is legible — a person who cannot see what happened cannot
/// meaningfully approve or revoke anything.
/// <para>
/// It goes through the full cognitive cycle rather than being a query. A briefing is a claim about
/// what happened, and claims are things Aurora decides to make.
/// </para>
/// </remarks>
public interface IReviewApplication
{
    Task<ReviewOutcome> ReviewAsync(ReviewRequest request, CancellationToken ct);
}
