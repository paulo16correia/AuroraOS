using System.Text.Json;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Consent;

/// <summary>
/// It.2 consent gate, first increment (design/0002). LOW-risk capabilities auto-grant.
/// A capability explicitly marked <see cref="CapabilityDescriptor.ApprovalRequired"/> is gated by
/// a persisted, one-time approval scoped to the exact action + input (see
/// <see cref="IApprovalStore"/>). Anything else at MEDIUM+ has no consent path yet and is refused.
/// </summary>
public sealed class PersistentApprovalConsentGate : IConsentGate
{
    private readonly IApprovalStore _approvals;

    public PersistentApprovalConsentGate(IApprovalStore approvals) => _approvals = approvals;

    public async Task<ConsentOutcome> EvaluateAsync(
        CapabilityDescriptor capability, JsonElement input, string scopeHash, Principal principal, CancellationToken ct)
    {
        if (capability.Risk == RiskLevel.Low && !capability.ApprovalRequired)
        {
            return new ConsentOutcome(true, new ConsentInfo(ConsentDecision.AutoLow, "policy"));
        }

        if (!capability.ApprovalRequired)
        {
            return new ConsentOutcome(false, new ConsentInfo(ConsentDecision.Denied, "policy"));
        }

        var evaluation = await _approvals.EvaluateAsync(principal, capability.ActionId, scopeHash, ct)
            .ConfigureAwait(false);

        return evaluation.Outcome switch
        {
            ApprovalOutcome.Consumed =>
                new ConsentOutcome(true, new ConsentInfo(ConsentDecision.Granted, "approval", evaluation.ApprovalId)),
            ApprovalOutcome.Rejected =>
                new ConsentOutcome(false, new ConsentInfo(ConsentDecision.Denied, "approval", evaluation.ApprovalId)),
            _ =>
                new ConsentOutcome(false, new ConsentInfo(ConsentDecision.RequiresApproval, "approval", evaluation.ApprovalId)),
        };
    }
}
