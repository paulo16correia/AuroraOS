namespace Aurora.Core.Contracts;

/// <summary>Kinds of tie the world model can hold (RFC 029).</summary>
public static class RelationType
{
    public const string Family = "FAMILY";
    public const string Professional = "PROFESSIONAL";
    public const string Contractual = "CONTRACTUAL";
    public const string Belonging = "BELONGING";
    public const string Dependency = "DEPENDENCY";
    public const string Contact = "CONTACT";
    public const string Property = "PROPERTY";

    public static bool IsKnown(string type) =>
        type is Family or Professional or Contractual or Belonging or Dependency or Contact or Property;
}

public static class RelationshipStatus
{
    public const string Proposed = "PROPOSED";
    public const string Active = "ACTIVE";

    /// <summary>Contested. On record, and not usable while it stands.</summary>
    public const string Disputed = "DISPUTED";

    /// <summary>Over. The interval closes; the past is not rewritten.</summary>
    public const string Ended = "ENDED";

    public const string Retracted = "RETRACTED";
}

/// <summary>
/// What a relationship lets Aurora do on someone's behalf (RFC 029).
/// </summary>
/// <remarks>
/// Always explicit, and <see cref="None"/> by default. "You are a client" is a fact about a tie and
/// says nothing about acting for anyone — rule 1 keeps relationship, permission and identity as
/// three separate objects, and this is the field where they would otherwise blur.
/// </remarks>
public static class AuthorityScope
{
    /// <summary>Knowing about the tie, and nothing else. The default, and almost always right.</summary>
    public const string None = "NONE";

    /// <summary>May be spoken to about it, when the person asked for that.</summary>
    public const string Correspond = "CORRESPOND";

    /// <summary>May act on the subject's behalf within a stated, separately approved limit.</summary>
    public const string ActOnBehalf = "ACT_ON_BEHALF";

    public static bool IsKnown(string scope) => scope is None or Correspond or ActOnBehalf;

    /// <summary>Scopes that need an approval to be claimed at all.</summary>
    public static bool NeedsApproval(string scope) => scope is Correspond or ActOnBehalf;
}

/// <summary>
/// A stated tie between two things, over an interval (RFC 029).
/// </summary>
/// <remarks>
/// Half-open <c>[valid_from, valid_to)</c>, the same shape the world model already uses, because
/// rule 4 requires the beginning and the end to survive: reassigning a relationship closes an
/// interval and opens another, and never rewrites what was true before.
/// </remarks>
public sealed record RelationshipAssertion(
    string Id,
    string SubjectRef,
    string RelationType,
    string ObjectRef,
    string QualifiersJson,
    string AuthorityScope,
    double Confidence,
    IReadOnlyList<string> EvidenceRefs,
    string ValidFromUtc,
    string? ValidToUtc,
    string Status,
    /// <summary>Who permitted storing this, when the subject is not the owner (rule 3).</summary>
    string? AuthorizationRef,
    string? RetentionUntilUtc);

public sealed record RelationshipCandidate(
    string SubjectRef,
    string RelationType,
    string ObjectRef,
    double Confidence,
    string QualifiersJson = "{}",
    string AuthorityScope = Contracts.AuthorityScope.None,
    string? ValidFromUtc = null,
    string? AuthorizationRef = null,
    TimeSpan? Retention = null);

public static class PreferenceBasis
{
    /// <summary>The person said so. The only basis that may act without confirmation.</summary>
    public const string Explicit = "EXPLICIT";

    public const string Observed = "OBSERVED";
    public const string Inferred = "INFERRED";

    public static bool IsKnown(string basis) => basis is Explicit or Observed or Inferred;
}

public static class PreferenceStatus
{
    public const string Candidate = "CANDIDATE";
    public const string Active = "ACTIVE";

    /// <summary>Displaced by something the person actually said.</summary>
    public const string Rejected = "REJECTED";

    public const string Expired = "EXPIRED";
}

/// <summary>What kind of thing a preference is about. Open by design, unlike authority.</summary>
public static class PreferenceDimension
{
    public const string Tool = "TOOL";
    public const string Format = "FORMAT";
    public const string Time = "TIME";
    public const string Tone = "TONE";
    public const string Technical = "TECHNICAL";
}

/// <summary>
/// How someone likes things done (RFC 029).
/// </summary>
/// <remarks>
/// A habit, not a trait. Preferences shape tone, format and choice of tool; they do not define
/// Aurora's personality and they confer no right to act — converting the first into the second is
/// exactly what the RFC's justification warns against.
/// </remarks>
public sealed record Preference(
    string Id,
    string OwnerRef,
    string SubjectRef,
    string Dimension,
    string ValueJson,
    double Strength,
    string Basis,
    IReadOnlyList<string> EvidenceRefs,
    string ScopeJson,
    string Status,
    string ReviewAtUtc,
    /// <summary>Whether acting on this needs the person to say yes first (rule 2).</summary>
    bool ConsentRequired);

/// <summary>
/// Preferences that apply, and whether they may be acted on unasked.
/// </summary>
/// <remarks>
/// The second field travels with the first for the same reason it does in the belief system: a
/// caller must not be able to obtain the preferences without also obtaining the answer about what
/// they license.
/// </remarks>
public sealed record PreferenceResolution(
    IReadOnlyList<Preference> Preferences,
    bool MayActWithoutConfirmation,
    string Reason);

/// <summary>The kinds of effect rule 2 puts behind a confirmation.</summary>
public static class PreferenceEffect
{
    /// <summary>Tone, ordering, formatting. Nothing leaves Aurora and nothing persists.</summary>
    public const string Presentational = "PRESENTATIONAL";

    public const string Purchase = "PURCHASE";
    public const string ExternalCommunication = "EXTERNAL_COMMUNICATION";
    public const string SensitiveData = "SENSITIVE_DATA";
    public const string PersistentChange = "PERSISTENT_CHANGE";

    public static bool NeedsConfirmation(string effect) => effect != Presentational;

    public static bool IsKnown(string effect) =>
        effect is Presentational or Purchase or ExternalCommunication
               or SensitiveData or PersistentChange;
}

public sealed class RelationshipException : Exception
{
    public RelationshipException(string message) : base(message)
    {
    }
}
