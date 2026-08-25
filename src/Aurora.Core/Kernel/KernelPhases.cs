using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Core.Kernel;

/// <summary>
/// What a request resolved to: a real capability, a validated input, and how it was arrived at.
/// </summary>
/// <remarks>
/// Only <see cref="AuroraKernel.ResolveAsync"/> constructs one. A caller cannot assemble a
/// resolution by hand and hand it to authorization — the phases are separable so cognition can run
/// between them, not so the checks can be skipped.
/// </remarks>
public sealed class ActionResolution
{
    internal ActionResolution(
        ResolvedAction resolved, ICapability capability, Principal principal,
        string inputHash, string requestHash, string? idempotencyKey)
    {
        Resolved = resolved;
        Capability = capability;
        Principal = principal;
        InputHash = inputHash;
        RequestHash = requestHash;
        IdempotencyKey = idempotencyKey;
    }

    public ResolvedAction Resolved { get; }

    /// <summary>The capability's declared risk, which is what a decision has to be priced against.</summary>
    public string Risk => Capability.Descriptor.Risk.ToString();

    /// <summary>Whether running this reaches outside Aurora.</summary>
    public bool HasExternalEffect => Capability.Descriptor.Effects.Count > 0;

    public Principal Principal { get; }

    internal ICapability Capability { get; }

    /// <summary>Hash of the canonical input; the parameters an action is proposed against.</summary>
    public string InputHash { get; }

    internal string RequestHash { get; }

    internal string? IdempotencyKey { get; }
}

/// <summary>An action cleared to run: permitted, consented, and holding its reservation.</summary>
public sealed class ActionAuthorization
{
    internal ActionAuthorization(
        ActionResolution resolution, IReadOnlyList<string> policyIds, ConsentInfo consent, bool reserved)
    {
        Resolution = resolution;
        PolicyIds = policyIds;
        Consent = consent;
        Reserved = reserved;
    }

    public ActionResolution Resolution { get; }

    public IReadOnlyList<string> PolicyIds { get; }

    public ConsentInfo Consent { get; }

    internal bool Reserved { get; }
}

/// <summary>
/// The result of a phase: exactly one of the two is set.
/// </summary>
/// <remarks>
/// A refusal is a finished <see cref="ExecuteResponse"/> rather than an error to interpret, because
/// the kernel has already audited it and settled whatever it reserved. The caller's job is to
/// return it, not to decide what it meant.
/// </remarks>
public sealed record ResolutionOutcome(ActionResolution? Resolution, ExecuteResponse? Refusal);

public sealed record AuthorizationOutcome(ActionAuthorization? Authorization, ExecuteResponse? Refusal);
