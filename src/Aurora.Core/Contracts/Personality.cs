namespace Aurora.Core.Contracts;

public static class ProfileStatus
{
    public const string Draft = "DRAFT";
    public const string Active = "ACTIVE";
    public const string Retired = "RETIRED";
}

/// <summary>
/// How Aurora sounds. Settings, not traits.
/// </summary>
/// <remarks>
/// Each is a dial a person can turn. None of them is a claim about what Aurora is — turning
/// <see cref="Humour"/> up does not make it amused, it makes its sentences lighter, and the
/// difference matters because the second is honest and the first is not.
/// </remarks>
public sealed record Voice(
    double Formality,
    double Conciseness,
    double Humour,
    double Proactivity)
{
    public static Voice Default { get; } = new(0.5, 0.7, 0.2, 0.4);

    /// <summary>
    /// The voice for a difficult subject: plain, brief, and not volunteering.
    /// </summary>
    /// <remarks>
    /// RFC 07's limit case for sensitive content. Humour goes to zero and proactivity with it —
    /// somebody dealing with something hard did not ask for a companion.
    /// </remarks>
    public Voice Sobered() => this with { Humour = 0, Proactivity = Math.Min(Proactivity, 0.1) };
}

/// <summary>A versioned communication identity (RFC 07).</summary>
public sealed record PersonalityProfile(
    string Id,
    int Version,
    string Name,
    IReadOnlyList<string> Languages,
    string DefaultLocale,
    Voice Voice,
    IReadOnlyList<string> Values,
    /// <summary>Things this profile may never say, whatever the tone settings are.</summary>
    IReadOnlyList<string> ProhibitedClaims,
    IReadOnlyList<string> InteractionRules,
    string DisclosureText,
    IReadOnlyList<string> EscalationRules,
    string ActiveFromUtc,
    string? ActiveToUtc,
    string Status,
    string? ApprovalRef = null);

/// <summary>How someone wants to be spoken to on a channel.</summary>
public sealed record CommunicationPreference(
    string OwnerId,
    string Channel,
    string Language,
    double Verbosity,
    string? QuietHours,
    string AccessibilityJson,
    bool ConsentForProactivity,
    string UpdatedAtUtc);

/// <summary>An audited change of identity (RFC 07 rule 1).</summary>
public sealed record IdentityChange(
    string Id,
    string ProfileId,
    int OldVersion,
    int NewVersion,
    string Actor,
    string Reason,
    string ApprovedAtUtc);

/// <summary>The profile and preference that apply, for one owner on one channel at one time.</summary>
public sealed record ResolvedProfile(
    PersonalityProfile Profile,
    CommunicationPreference Preference,
    Voice EffectiveVoice,
    string Locale,
    /// <summary>Whether Aurora is falling back because the real profile could not be read.</summary>
    bool Degraded,
    string Reason);

/// <summary>What a segment of a message is for. A closed set, and there is no persuasive kind.</summary>
public static class SegmentKind
{
    /// <summary>What Aurora is. Mandatory where the channel or the context warrants it (rule 2).</summary>
    public const string Disclosure = "DISCLOSURE";

    public const string Content = "CONTENT";

    /// <summary>A risk, stated plainly. Tone settings cannot touch these (rule 3).</summary>
    public const string Risk = "RISK";

    /// <summary>Where to go for real help. Never softened, never omitted.</summary>
    public const string Escalation = "ESCALATION";

    public static bool IsKnown(string kind) =>
        kind is Disclosure or Content or Risk or Escalation;

    /// <summary>Kinds the voice may not reshape or remove.</summary>
    public static bool IsProtected(string kind) => kind is Disclosure or Risk or Escalation;
}

public sealed record MessageSegment(string Kind, string Text);

/// <summary>
/// What Aurora hands the client to say, and the terms it must be said on.
/// </summary>
/// <remarks>
/// Not prose. RFC 021 leaves the wording to the LLM client, so this carries the substance, the
/// voice to render it in, and the claims that may not appear — a draft the client dresses rather
/// than a sentence Aurora wrote.
/// </remarks>
public sealed record MessageDraft(
    IReadOnlyList<MessageSegment> Segments,
    Voice Voice,
    string Locale,
    IReadOnlyList<string> ProhibitedClaims,
    bool DisclosureRequired);

/// <summary>What the cycle decided to communicate, before it has a voice.</summary>
public sealed record ResponsePlan(
    IReadOnlyList<MessageSegment> Segments,
    string Channel,
    /// <summary>Whether the subject is one where lightness and volunteering are out of place.</summary>
    bool SensitiveSubject = false);

public sealed class PersonalityException : Exception
{
    public PersonalityException(string message) : base(message)
    {
    }
}
