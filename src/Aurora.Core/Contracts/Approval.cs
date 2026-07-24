using System.Text.Json.Serialization;

namespace Aurora.Core.Contracts;

/// <summary>Lifecycle of a persisted approval, scoped to one (principal, action_id, scope_hash).</summary>
public static class ApprovalStatus
{
    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Consumed = "CONSUMED";
}

/// <summary>A persisted approval row.</summary>
public sealed record ApprovalRecord(
    string ApprovalId,
    string PrincipalClientId,
    string PrincipalWindowsUser,
    string ActionId,
    string ScopeHash,
    string Status,
    string CreatedAtUtc,
    string ExpiresAtUtc,
    string? DecidedAtUtc);

/// <summary>Outcome of evaluating (principal, action_id, scope_hash) against the approval ledger.</summary>
public enum ApprovalOutcome
{
    /// <summary>A live APPROVED record was found and has just been consumed (one-time use).</summary>
    Consumed,

    /// <summary>No live decision exists yet; a PENDING record (new or pre-existing) awaits one.</summary>
    Pending,

    /// <summary>A REJECTED record exists for this exact scope.</summary>
    Rejected,
}

/// <summary>Result of <see cref="Abstractions.IApprovalStore.EvaluateAsync"/>.</summary>
public sealed record ApprovalEvaluation(ApprovalOutcome Outcome, string ApprovalId);

/// <summary>Outcome of deciding a specific approval id.</summary>
public enum ApprovalDecideOutcome
{
    Decided,
    NotFound,
    NotPending,
}

/// <summary>Result of <see cref="Abstractions.IApprovalStore.DecideAsync"/>.</summary>
public sealed record ApprovalDecideResult(ApprovalDecideOutcome Outcome, ApprovalRecord? Record);

/// <summary>Allowed values of <see cref="ApproveRequest.Decision"/>.</summary>
public static class ApprovalDecision
{
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}

/// <summary>Input of <c>aurora_approve</c>. Unknown members are rejected.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ApproveRequest(string? ApprovalId = null, string? Decision = null);

/// <summary>Terminal status of <c>aurora_approve</c>.</summary>
public static class ApproveStatus
{
    public const string Decided = "decided";
    public const string NotFound = "not_found";
    public const string NotPending = "not_pending";
    public const string Invalid = "invalid";
}

/// <summary>Output of <c>aurora_approve</c>.</summary>
public sealed record ApproveResponse(
    string Status,
    string? ApprovalId = null,
    string? ApprovalState = null,
    string? ActionId = null,
    IReadOnlyList<string>? AuditRef = null,
    ExecuteError? Error = null);
