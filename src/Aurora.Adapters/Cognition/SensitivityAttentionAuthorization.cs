using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Cognition;

/// <summary>
/// Baseline authorisation: a candidate above the caller's ceiling is never considered
/// (RFC 023 rules 1 and 4).
/// </summary>
/// <remarks>
/// Rule 4 is the one that matters here — "a secret or hostile instruction does not gain access
/// because it is urgent". Keeping this decision out of the scoring function means urgency has
/// nothing to bid with: an unauthorised item is gone before any weight is applied.
/// </remarks>
public sealed class SensitivityAttentionAuthorization : IAttentionAuthorization
{
    public bool MayConsider(AttentionItem candidate, MemoryAccessContext access) =>
        Sensitivity.IsKnown(candidate.SensitivityClass)
        && Sensitivity.Rank(candidate.SensitivityClass) <= Sensitivity.Rank(access.MaxSensitivity);
}
