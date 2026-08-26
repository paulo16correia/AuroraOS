using System.Text.Json;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Policy;

/// <summary>
/// Fail-closed / default-deny policy engine.
/// </summary>
/// <remarks>
/// Three ways through, decided on risk, effects, approval and reversibility rather than on
/// hardcoded action ids, so a new capability is judged by what it says about itself:
/// <list type="bullet">
/// <item><b>LOW with no declared effects</b> — genuinely read-only, so no approval.</item>
/// <item><b>MEDIUM that opted into approval</b> — a person says yes once, scoped to that input.</item>
/// <item><b>HIGH that opted into approval <i>and</i> declares itself reversible</b> — because at
/// HIGH, one yes is not enough on its own. If it goes wrong, somebody has to be able to put it
/// back, and a capability that cannot say how is not one a default policy should permit.</item>
/// </list>
/// Everything else is denied, including a MEDIUM capability that did not opt into approval and
/// anything at CRITICAL, which nothing ships at.
/// </remarks>
public sealed class AllowlistPolicyEngine : IPolicyEngine
{
    /// <summary>Bump this whenever the rules below change; live consent sessions then stop matching.</summary>
    public string Version => "allowlist-v2";

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

        if (capability is { Risk: RiskLevel.High, ApprovalRequired: true, Reversible: true })
        {
            return PolicyDecision.Allow("policy.high_requires_approval_and_reversibility");
        }

        return PolicyDecision.Deny("Capability is not permitted by policy.", "policy.default_deny");
    }
}
