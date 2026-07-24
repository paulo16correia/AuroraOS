using System.Text.Json;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Policy;

/// <summary>
/// Fail-closed / default-deny policy engine. Permits a capability when it is genuinely low-risk
/// and side-effect free (LOW risk with no declared effects), or when it is MEDIUM risk and has
/// explicitly opted into the approval-gated path (see <see cref="Abstractions.IConsentGate"/>);
/// everything else — including any MEDIUM capability that has NOT opted into approval — is denied.
/// Decided on risk/effects/approval rather than hardcoded action ids so it generalizes to new
/// capabilities.
/// </summary>
public sealed class AllowlistPolicyEngine : IPolicyEngine
{
    public PolicyDecision Evaluate(CapabilityDescriptor capability, JsonElement input, Principal principal)
    {
        if (capability.Risk == RiskLevel.Low && capability.Effects.Count == 0)
        {
            return PolicyDecision.Allow("policy.low_readonly");
        }

        if (capability.Risk == RiskLevel.Medium && capability.ApprovalRequired)
        {
            return PolicyDecision.Allow("policy.medium_requires_approval");
        }

        return PolicyDecision.Deny("Capability is not permitted by policy.", "policy.default_deny");
    }
}
