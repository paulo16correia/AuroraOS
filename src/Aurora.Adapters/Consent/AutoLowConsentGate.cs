using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Consent;

/// <summary>
/// It.0 consent gate: LOW-risk capabilities auto-grant; anything at MEDIUM or above requires
/// approval that cannot yet be satisfied (real consent sessions arrive in It.2).
/// </summary>
public sealed class AutoLowConsentGate : IConsentGate
{
    public ConsentOutcome Evaluate(CapabilityDescriptor capability, Principal principal)
    {
        if (capability.Risk == RiskLevel.Low && !capability.ApprovalRequired)
        {
            return new ConsentOutcome(true, new ConsentInfo(ConsentDecision.AutoLow, "policy"));
        }

        return new ConsentOutcome(false, new ConsentInfo(ConsentDecision.RequiresApproval, "session"));
    }
}
