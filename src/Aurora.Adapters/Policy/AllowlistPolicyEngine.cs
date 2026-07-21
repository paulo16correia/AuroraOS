using System.Text.Json;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Policy;

/// <summary>
/// Fail-closed / default-deny policy engine. Permits a capability only when it is genuinely
/// low-risk and side-effect free (LOW risk with no declared effects); everything else is denied.
/// Decided on risk/effects rather than hardcoded action ids so it generalizes to new capabilities.
/// </summary>
public sealed class AllowlistPolicyEngine : IPolicyEngine
{
    public PolicyDecision Evaluate(CapabilityDescriptor capability, JsonElement input, Principal principal)
    {
        if (capability.Risk == RiskLevel.Low && capability.Effects.Count == 0)
        {
            return PolicyDecision.Allow("policy.low_readonly");
        }

        return PolicyDecision.Deny("Capability is not permitted by policy.", "policy.default_deny");
    }
}
