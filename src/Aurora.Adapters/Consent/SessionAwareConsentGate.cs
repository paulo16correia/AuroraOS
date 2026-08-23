using System.Text.Json;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Consent;

/// <summary>
/// It.2 consent gate, complete (docs/adr/0010). LOW auto-grants; an approval-gated capability is
/// authorised either by a live read-only session or by a one-time approval bound to the exact
/// action and input; everything else at MEDIUM+ stays refused.
/// </summary>
/// <remarks>
/// The rule that shapes this class: <b>a session never covers a capability that declares
/// effects.</b> Design 0001 named the danger precisely — a reused session running subsequent
/// writes is permanent autonomy with effects, which is not what a single human approval consented
/// to. Reads are different in kind: repeating a read changes nothing, so amortising one approval
/// across several of them costs the user no authority they did not already grant.
/// <para>
/// A session is also capped at MEDIUM. A HIGH or CRITICAL capability is never covered by reuse,
/// even when it is read-only, because the reason it is HIGH is usually the sensitivity of what it
/// reads.
/// </para>
/// </remarks>
public sealed class SessionAwareConsentGate : IConsentGate
{
    private readonly IApprovalStore _approvals;
    private readonly IConsentSessionStore _sessions;

    public SessionAwareConsentGate(IApprovalStore approvals, IConsentSessionStore sessions)
    {
        _approvals = approvals;
        _sessions = sessions;
    }

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

        var sessionEligible = capability.Effects.Count == 0 && capability.Risk <= RiskLevel.Medium;

        if (sessionEligible)
        {
            ConsentSessionUse use = await _sessions.TryUseAsync(principal, ct).ConfigureAwait(false);
            if (use.Outcome == ConsentSessionUseOutcome.Used)
            {
                return new ConsentOutcome(
                    true, new ConsentInfo(ConsentDecision.Granted, "session", SessionId: use.SessionId));
            }
        }

        ApprovalEvaluation evaluation =
            await _approvals.EvaluateAsync(principal, capability.ActionId, scopeHash, ct).ConfigureAwait(false);

        switch (evaluation.Outcome)
        {
            case ApprovalOutcome.Consumed:
                // Approving a read opens the session that will cover the next ones. Approving a
                // write opens nothing: the next write costs another explicit decision.
                string? sessionId = null;
                if (sessionEligible)
                {
                    ConsentSession session = await _sessions.OpenAsync(principal, ct).ConfigureAwait(false);
                    sessionId = session.SessionId;
                }

                return new ConsentOutcome(
                    true,
                    new ConsentInfo(ConsentDecision.Granted, "approval", evaluation.ApprovalId, sessionId));

            case ApprovalOutcome.Rejected:
                return new ConsentOutcome(
                    false, new ConsentInfo(ConsentDecision.Denied, "approval", evaluation.ApprovalId));

            default:
                return new ConsentOutcome(
                    false, new ConsentInfo(ConsentDecision.RequiresApproval, "approval", evaluation.ApprovalId));
        }
    }
}
