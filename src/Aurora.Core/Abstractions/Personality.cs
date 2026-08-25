using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// A consistent, editable and auditable communication identity (RFC 07).
/// </summary>
/// <remarks>
/// Versioning identity is what lets it evolve without losing continuity. Keeping it separate from
/// response generation is what stops an informal prompt becoming an invisible, unauditable rule —
/// which is the failure mode this whole RFC is written against.
/// </remarks>
public interface IPersonalityService
{
    /// <summary>
    /// The profile and preference in force for an owner on a channel at a moment.
    /// </summary>
    /// <remarks>
    /// Never fails. If no profile can be read, it returns the minimum safe one and says it is
    /// degraded, because inventing a personality is worse than admitting there is none.
    /// </remarks>
    Task<ResolvedProfile> ResolveAsync(
        string ownerId, string channel, DateTimeOffset at, CancellationToken ct);

    /// <summary>Records a proposed identity. DRAFT until somebody approves it.</summary>
    Task<PersonalityProfile> ProposeAsync(PersonalityProfile profile, CancellationToken ct);

    /// <summary>
    /// Makes a drafted profile the active one. Needs the owner's approval (rule 1).
    /// </summary>
    Task<PersonalityProfile> ActivateAsync(
        string profileId, string approvalRef, string actor, string reason, CancellationToken ct);

    /// <summary>How the identity got to where it is.</summary>
    Task<IReadOnlyList<IdentityChange>> HistoryAsync(CancellationToken ct);

    Task<CommunicationPreference> SetPreferenceAsync(
        CommunicationPreference preference, CancellationToken ct);
}

/// <summary>
/// Turns what the cycle decided to say into terms it may be said on (RFC 07).
/// </summary>
/// <remarks>
/// The composer refuses more than it produces. Personality may shape ordinary content and may not
/// touch a risk, a disclosure or an escalation; and it may never manufacture urgency, dependence,
/// guilt or exclusivity, which is the one rule in this RFC about manipulating a person rather than
/// about sounding right.
/// </remarks>
public interface IComposer
{
    MessageDraft Render(ResponsePlan plan, ResolvedProfile profile);
}
