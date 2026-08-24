namespace Aurora.Core.Contracts;

public static class AttentionKind
{
    public const string Event = "EVENT";
    public const string Memory = "MEMORY";
    public const string Goal = "GOAL";
    public const string Observation = "OBSERVATION";
    public const string Alert = "ALERT";
}

public static class AttentionSetStatus
{
    public const string Proposed = "PROPOSED";
    public const string Locked = "LOCKED";
    public const string Released = "RELEASED";
}

/// <summary>Why an item was selected or left out (RFC 023 rule 3).</summary>
public static class AttentionReason
{
    public const string Selected = "selected";
    public const string BelowThreshold = "below_threshold";
    public const string ItemLimitReached = "item_limit_reached";
    public const string BudgetExhausted = "budget_exhausted";
    public const string NotAuthorised = "not_authorised";
    public const string AboveSensitivityCeiling = "above_sensitivity_ceiling";
    public const string Expired = "expired";
}

/// <summary>One candidate for a cycle's focus (RFC 023).</summary>
public sealed record AttentionItem(
    string Ref,
    string Kind,
    double Relevance,
    double Urgency,
    double Novelty,
    double Impact,
    double Confidence,
    double Recency,
    string SensitivityClass,
    int TokenCost,
    string? ExpiresAtUtc = null,
    double Score = 0,
    IReadOnlyList<string>? ReasonCodes = null);

/// <summary>The bounded set a cycle will actually think about (RFC 023).</summary>
public sealed record AttentionSet(
    string Id,
    string CycleId,
    IReadOnlyList<AttentionItem> Items,
    IReadOnlyList<AttentionItem> Excluded,
    int TokenBudget,
    int ItemLimit,
    string Status,
    string SelectedAtUtc);

/// <summary>Weights and limits for ranking (RFC 023).</summary>
public sealed record AttentionPolicy(
    string Id,
    string Scope,
    double RelevanceWeight,
    double UrgencyWeight,
    double NoveltyWeight,
    double ImpactWeight,
    double ConfidenceWeight,
    double RecencyWeight,
    double SelectionThreshold,
    int MaxItems,
    int TokenBudget,
    string SensitivityCeiling,
    int Version)
{
    public static readonly AttentionPolicy Default = new(
        "attention/default", "global", 0.3, 0.25, 0.1, 0.2, 0.1, 0.05,
        SelectionThreshold: 0.2, MaxItems: 8, TokenBudget: 4000,
        SensitivityCeiling: Sensitivity.Confidential, Version: 1);
}
