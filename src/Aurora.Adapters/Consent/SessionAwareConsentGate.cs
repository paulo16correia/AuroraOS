using System.Text.Json;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Consent;

/// <summary>
/// It.2 consent gate, complete (docs/adr/0010). LOW auto-grants; an approval-gated capability is
/// authorised either by a live session that covers it or by a one-time approval bound to the
/// exact action and input; everything else at MEDIUM+ stays refused.
/// </summary>
/// <remarks>
/// The rule that shapes this class: <b>a session covers a capability with effects only when the
/// session named that capability.</b> Design 0001 named the danger precisely — a reused session
/// running subsequent writes is permanent autonomy with effects, which is not what a single human
/// approval consented to. Reads are different in kind: repeating a read changes nothing, so
/// amortising one approval across several of them costs the user no authority they did not
/// already grant.
/// <para>
/// A named window (docs/adr/0070) does not relax that: it makes the write nameable. The user is
/// asked about one action, in words, with a budget and a deadline, and the window pays for that
/// action and nothing else — an unnamed capability, effectful or not, cannot spend it, and an
/// unnamed window cannot pay for an effect. What is refused throughout is the unnamed write: a
/// session opened for reading has never covered one, and still does not.
/// </para>
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

        var effectFree = capability.Effects.Count == 0;
        var sessionEligible = effectFree && capability.Risk <= RiskLevel.Medium;

        if (capability.Risk <= RiskLevel.Medium)
        {
            // Effect-free work spends an ordinary window; an effect can only spend a window that
            // named it. The store decides which, so neither kind can pay for the other by accident.
            ConsentSessionUse use = effectFree
                ? await _sessions.TryUseAsync(principal, ct).ConfigureAwait(false)
                : await _sessions.TryUseAsync(principal, capability.ActionId, ct).ConfigureAwait(false);

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
                // write opens nothing: the next write costs another explicit decision, unless a
                // window was opened for it by name — and a window is never a side effect of one
                // approval, only of a capability that asked for it in those words.
                string? sessionId = null;
                if (capability.OpensWindow is { } window)
                {
                    ConsentSession named = await _sessions
                        .OpenAsync(principal, window.Actions, window.Lifetime, window.MaxActions, ct)
                        .ConfigureAwait(false);
                    sessionId = named.SessionId;
                }
                else if (sessionEligible)
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
