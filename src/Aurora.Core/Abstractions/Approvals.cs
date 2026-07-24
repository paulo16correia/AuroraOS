using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// Persisted approval ledger for capabilities that require explicit consent beyond auto-LOW.
/// An approval is scoped to the exact (principal, action_id, scope_hash) that requested it; any
/// change to the input changes the scope hash and requires a fresh approval.
/// </summary>
public interface IApprovalStore
{
    /// <summary>
    /// Evaluates the live approval state for this exact scope. If a live APPROVED record exists it
    /// is atomically consumed (one-time use). Otherwise returns the existing live PENDING or
    /// REJECTED record, or creates a new PENDING one when none exists.
    /// </summary>
    Task<ApprovalEvaluation> EvaluateAsync(Principal principal, string actionId, string scopeHash, CancellationToken ct);

    /// <summary>Applies a human decision to a live PENDING approval owned by <paramref name="principal"/>.</summary>
    Task<ApprovalDecideResult> DecideAsync(Principal principal, string approvalId, bool approve, CancellationToken ct);
}
