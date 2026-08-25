namespace Aurora.Core.Contracts;

public static class MissionStatus
{
    public const string Draft = "DRAFT";
    public const string Active = "ACTIVE";
    public const string Paused = "PAUSED";
    public const string Retired = "RETIRED";

    public static bool IsKnown(string status) =>
        status is Draft or Active or Paused or Retired;
}

/// <summary>
/// An enduring purpose, and the limits of pursuing it (RFC 052).
/// </summary>
/// <remarks>
/// A mission is not an execution order. It says what Aurora is for over weeks and months so that
/// individual goals stop being unrelated errands — and it never becomes a reason to do something
/// the Constitution, the Laws or the policies would otherwise refuse.
/// </remarks>
public sealed record Mission(
    string Id,
    string MindId,
    string Title,
    string Purpose,
    /// <summary>How the mission would be known to be succeeding. Required, like a goal's.</summary>
    string SuccessDefinition,
    /// <summary>
    /// What this mission does not extend to. Stated by the owner rather than inferred, because a
    /// purpose with no stated edge quietly grows one.
    /// </summary>
    IReadOnlyList<string> Boundaries,
    string PriorityPolicy,
    string Owner,
    string Status,
    string? ReviewAtUtc,
    IReadOnlyList<string> EvidenceRefs,
    string CreatedAtUtc,
    string? ApprovalRef = null);

public sealed record MissionDefinition(
    string Title,
    string Purpose,
    string SuccessDefinition,
    IReadOnlyList<string> Boundaries,
    string Owner,
    string MindId = "mind/local",
    string PriorityPolicy = "owner_first",
    string? ReviewAtUtc = null);

/// <summary>What a review found, without changing anything on its own.</summary>
public sealed record MissionReview(
    string MissionId,
    string ReviewedAtUtc,
    IReadOnlyList<string> AlignedGoalRefs,
    /// <summary>Goals belonging to nobody's mission whose review date has passed.</summary>
    IReadOnlyList<string> AdHocGoalsPastReview,
    bool ReviewOverdue,
    string Summary);

public sealed class MissionException : Exception
{
    public MissionException(string message) : base(message)
    {
    }
}
