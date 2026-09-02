namespace Aurora.Core.Contracts;

/// <summary>
/// Where Aurora is speaking. A transport, not a personality (docs/adr/0073).
/// </summary>
/// <remarks>
/// The whole point of naming these in one enum is that they are interchangeable. Aurora on the
/// telephone is the same Aurora as Aurora in a Discord call and the same one as Aurora in a text
/// conversation — what differs is how the audio gets there and what the channel can carry.
/// <para>
/// A channel never becomes a source of truth. Identity, memory, missions and permissions live in
/// Aurora; a channel is where a conversation happens to occur.
/// </para>
/// </remarks>
public enum VoiceChannel
{
    /// <summary>The public telephone network, reached through a provider.</summary>
    Phone,

    /// <summary>A Discord voice channel. Aurora's first voice, and the only verified one.</summary>
    Discord,

    /// <summary>Microsoft Teams. Not implemented; the abstraction exists so it can be.</summary>
    Teams,
}

/// <summary>Who dialled.</summary>
/// <remarks>
/// The most consequential distinction in this file. An inbound call is somebody choosing to speak
/// to Aurora; an outbound call is Aurora choosing to speak to somebody, which needs a reason, a
/// scope, an expiry and a person who authorised it (<see cref="OutboundCallIntent"/>).
/// </remarks>
public enum VoiceCallDirection
{
    Inbound,
    Outbound,
}

/// <summary>Where a voice session is in its life.</summary>
public enum VoiceSessionState
{
    /// <summary>Created and authorised, nothing connected yet.</summary>
    Pending,

    /// <summary>The transport is being established.</summary>
    Connecting,

    /// <summary>Audio is flowing and the interaction layer is up.</summary>
    Active,

    /// <summary>Winding down deliberately — a hangup in progress, a budget reached.</summary>
    Ending,

    /// <summary>Over, for an ordinary reason.</summary>
    Ended,

    /// <summary>Over because something refused, broke or timed out.</summary>
    Failed,

    /// <summary>Over because a person or a policy stopped it.</summary>
    Cancelled,
}

/// <summary>
/// Somebody on the other end of a voice session, and how much is actually known about them.
/// </summary>
/// <param name="Handle">
/// What the channel calls them — an E.164 number, a Discord user id, a Teams object id.
/// </param>
/// <param name="DisplayName">What the channel says they are called. Attacker-controlled.</param>
/// <param name="IdentityRef">
/// The Aurora identity this was resolved to, if any. Null for somebody Aurora does not know.
/// </param>
/// <param name="Verification">How much the channel's claim about who this is can be believed.</param>
/// <remarks>
/// A participant is an identity, never an authority. Resolving a caller to somebody Aurora knows
/// well changes the tone of the conversation and changes nothing about what may be done in it.
/// </remarks>
public sealed record VoiceParticipant(
    string Handle,
    string? DisplayName = null,
    string? IdentityRef = null,
    ParticipantVerification Verification = ParticipantVerification.Unverified);

/// <summary>How far a channel's claim about who is speaking can be trusted.</summary>
/// <remarks>
/// Deliberately not a boolean. Caller ID on the public telephone network is trivially forged and is
/// nonetheless the only identifier most calls carry, so "the provider told us a number" has to be
/// representable as the weak evidence it is rather than collapsing into "verified" or "unknown".
/// </remarks>
public enum ParticipantVerification
{
    /// <summary>Nothing is known about who this is.</summary>
    Unverified,

    /// <summary>
    /// The channel asserted an identifier and cannot vouch for it. Caller ID lives here.
    /// </summary>
    ChannelAsserted,

    /// <summary>
    /// The channel authenticated them — a signed-in Discord or Teams account, or attestation such
    /// as STIR/SHAKEN where the provider actually supplies it.
    /// </summary>
    ChannelAuthenticated,
}

/// <summary>
/// What one voice session is allowed to do. The authority, held apart from the identity.
/// </summary>
/// <param name="AllowedActions">
/// Exhaustive. Action ids the session may ask for, and nothing else is reachable from inside it —
/// however convincingly somebody on the call asks.
/// </param>
/// <param name="MaxToolCalls">How many capability requests the whole session may make.</param>
/// <param name="MaxDuration">How long it may last before it is ended.</param>
/// <param name="ExpiresAtUtc">When the grant stops being usable, whatever the session is doing.</param>
/// <param name="DisclosureRequired">
/// Whether Aurora must say what it is at the start. RFC 07 rule 2 asks for it where the channel or
/// the context warrants it, and a telephone call to somebody who did not initiate it warrants it.
/// </param>
/// <remarks>
/// A grant is issued when a session is created and does not grow. Nothing said during a call adds
/// to it: not a request, not a relationship, not a mission, not a memory of having done it before.
/// The list is what the Kernel checks against, and the Kernel is what decides.
/// </remarks>
public sealed record VoiceGrant(
    IReadOnlyList<string> AllowedActions,
    int MaxToolCalls,
    TimeSpan MaxDuration,
    string ExpiresAtUtc,
    bool DisclosureRequired = true)
{
    /// <summary>Whether this grant names an action at all. Not whether the Kernel will allow it.</summary>
    /// <remarks>
    /// Two different questions and both have to be asked. This one is the session's own ceiling;
    /// the Kernel's answer is about policy, approval and the caller's principal. A session may name
    /// an action the Kernel then refuses, and that is the ordinary case rather than a bug.
    /// </remarks>
    public bool Names(string actionId) =>
        AllowedActions.Contains(actionId, StringComparer.Ordinal);
}

/// <summary>
/// Why Aurora is calling somebody, decided before it does.
/// </summary>
/// <param name="Purpose">Why, in a sentence a person approved.</param>
/// <param name="Objective">What would count as having succeeded.</param>
/// <param name="Target">Who is being called.</param>
/// <param name="Grant">What the call may do, and for how long.</param>
/// <param name="Constraints">
/// What it may not do or disclose, in words. Not enforced by string matching — the enforcement is
/// the grant's action list and the Kernel — but carried so the interaction layer is told, the
/// audit records it, and a person reviewing the call can see what was promised.
/// </param>
/// <param name="AuthorizedBy">The actor who approved this. Never "the planner" and never a mission.</param>
/// <param name="ApprovalRef">The approval record that authorised it.</param>
/// <remarks>
/// An outbound call is Aurora making something happen in somebody else's day, so it needs all of
/// this before it starts. The rule this exists to enforce: <b>a mission may create a goal and a
/// planner may propose a task, and neither is an authorisation.</b> Something has to have gone
/// through the approval path and produced a reference, or there is no intent and no call.
/// </remarks>
public sealed record OutboundCallIntent(
    string Purpose,
    string Objective,
    VoiceParticipant Target,
    VoiceGrant Grant,
    IReadOnlyList<string> Constraints,
    string AuthorizedBy,
    string ApprovalRef);

/// <summary>One voice session, as Aurora holds it.</summary>
/// <param name="SessionId">Aurora's own identifier. Not the provider's.</param>
/// <param name="Channel">Which transport.</param>
/// <param name="Provider">Who is carrying it — "twilio", "discord". For the audit and for support.</param>
/// <param name="Direction">Who dialled.</param>
/// <param name="ExternalRef">
/// The provider's identifier for the same conversation, so a support conversation about one
/// specific call is possible afterwards.
/// </param>
/// <param name="CorrelationId">
/// What ties every event of this session together in the audit, across processes.
/// </param>
/// <param name="Intent">Present for an outbound call. Null for one somebody made to Aurora.</param>
public sealed record VoiceSession(
    string SessionId,
    VoiceChannel Channel,
    string Provider,
    VoiceCallDirection Direction,
    VoiceParticipant Participant,
    VoiceGrant Grant,
    VoiceSessionState State,
    string StartedAtUtc,
    string CorrelationId,
    string? ExternalRef = null,
    string? EndedAtUtc = null,
    string? EndedReason = null,
    int ToolCallsUsed = 0,
    OutboundCallIntent? Intent = null)
{
    public bool IsLive => State is VoiceSessionState.Pending
        or VoiceSessionState.Connecting or VoiceSessionState.Active;
}

/// <summary>
/// The context one Realtime tool request is decided in.
/// </summary>
/// <remarks>
/// Carried explicitly rather than looked up from ambient state, because the thing that must never
/// happen is one session's request being decided against another session's authority. Two calls
/// arriving at once are two of these.
/// </remarks>
public sealed record VoiceToolContext(
    string SessionId,
    string CorrelationId,
    string RequestId,
    string ActionId,
    string InputJson);

/// <summary>Why a voice tool request was not run.</summary>
public static class VoiceRefusal
{
    /// <summary>The session's grant does not name this action.</summary>
    public const string NotInGrant = "not_in_grant";

    /// <summary>The session has spent its tool budget.</summary>
    public const string BudgetSpent = "budget_spent";

    /// <summary>The grant expired, or the session's maximum duration passed.</summary>
    public const string Expired = "expired";

    /// <summary>The session is not live.</summary>
    public const string NotLive = "not_live";

    /// <summary>Voice is stopped, by an operator or by policy.</summary>
    public const string VoiceStopped = "voice_stopped";

    /// <summary>The Kernel refused it. The ordinary case, and not an error.</summary>
    public const string KernelRefused = "kernel_refused";
}

/// <summary>
/// What the interaction layer is told about a tool request.
/// </summary>
/// <param name="Outcome">
/// One of four, and they are not interchangeable. The interaction layer must say what this says:
/// a call that failed is not one that succeeded, and one whose outcome is unknown is neither.
/// </param>
/// <remarks>
/// The reason this is a closed set with a written meaning: a language model asked to narrate an
/// outcome will narrate a plausible one unless it is handed an unambiguous one. Sending an email is
/// the case that matters — "I've sent it" said about a request that timed out is a lie the person
/// on the call has no way to detect.
/// </remarks>
public sealed record VoiceToolOutcome(
    string RequestId,
    VoiceToolResult Outcome,
    string? ResultJson = null,
    string? Refusal = null,
    string? Detail = null);

/// <summary>The four things that can have happened to a tool request.</summary>
public enum VoiceToolResult
{
    /// <summary>It ran and Aurora knows it worked.</summary>
    Completed,

    /// <summary>Aurora would not do it. Said naturally, and never worked around.</summary>
    Refused,

    /// <summary>It ran and did not work. Not to be narrated as success.</summary>
    Failed,

    /// <summary>
    /// It may have happened. The one that must never be flattened into either neighbour.
    /// </summary>
    Unknown,
}
