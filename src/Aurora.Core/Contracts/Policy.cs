namespace Aurora.Core.Contracts;

/// <summary>
/// Result of a fail-closed policy evaluation, performed with the concrete input immediately
/// before the effect. Default is deny.
/// </summary>
public sealed record PolicyDecision(bool Allowed, IReadOnlyList<string> PolicyIds, string? Reason = null)
{
    public static PolicyDecision Allow(params string[] policyIds) => new(true, policyIds);

    public static PolicyDecision Deny(string reason, params string[] policyIds) => new(false, policyIds, reason);
}

/// <summary>Outcome of a consent gate: whether the effect may proceed, plus the wire-facing info.</summary>
public sealed record ConsentOutcome(bool Granted, ConsentInfo Info);
