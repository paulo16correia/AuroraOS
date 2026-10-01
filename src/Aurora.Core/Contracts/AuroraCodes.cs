namespace Aurora.Core.Contracts;

/// <summary>Stable, machine-readable error codes for <see cref="ExecuteError.Code"/>.</summary>
public static class ErrorCodes
{
    public const string BothModes = "both_modes";
    public const string NoMode = "no_mode";
    public const string ObjectiveUnavailable = "objective_mode_unavailable";
    public const string KeywordRestricted = "keyword_resolution_restricted";
    public const string UnknownAction = "unknown_action";
    public const string SchemaInvalid = "schema_invalid";
    public const string InputTooLarge = "input_too_large";
    public const string ObjectiveTooLong = "objective_too_long";
    public const string KeyTooLong = "idempotency_key_too_long";
    public const string PolicyDenied = "policy_denied";
    public const string ConsentRequired = "consent_required";
    public const string ApprovalRequired = "approval_required";
    public const string IdempotencyConflict = "idempotency_conflict";
    public const string ExecutionInProgress = "execution_in_progress";
    public const string ExecutionFailed = "execution_failed";
    public const string UnknownState = "unknown_state";
    public const string ApprovalIdRequired = "approval_id_required";
    public const string InvalidDecision = "invalid_decision";
    public const string ApprovalNotPending = "approval_not_pending";
    public const string ApprovalNotFound = "approval_not_found";
    public const string PassphraseRequired = "passphrase_required";
    public const string PassphraseInvalid = "passphrase_invalid";
    public const string PassphraseLockedOut = "passphrase_locked_out";

    /// <summary>
    /// The action was permitted, and Aurora decided against it anyway (RFC 022).
    /// </summary>
    /// <remarks>
    /// Distinct from a denial: nothing refused this. Reporting it as <c>policy_denied</c> would
    /// send the caller looking for a rule that does not exist.
    /// </remarks>
    public const string NotChosen = "not_chosen";

    /// <summary>Aurora needs the person's input before it will act (RFC 022 ASK).</summary>
    public const string ClarificationRequired = "clarification_required";
}

/// <summary>Hard input limits enforced by the kernel before any effect.</summary>
public static class AuroraLimits
{
    /// <summary>Maximum canonical size (bytes) of an action input.</summary>
    public const int MaxInputBytes = 16 * 1024;

    /// <summary>Maximum length of a natural-language objective.</summary>
    public const int MaxObjectiveChars = 4 * 1024;

    /// <summary>Maximum length of an idempotency key.</summary>
    public const int MaxIdempotencyKeyChars = 200;

    /// <summary>
    /// Maximum size of a single plugin protocol frame (one JSON line), in UTF-16 code units.
    /// </summary>
    /// <remarks>
    /// The service protocol is line-delimited JSON, and nothing on the wire bounds a line — a
    /// plugin that emitted one enormous line would make the host buffer all of it (F-4,
    /// docs/adr/0079). One mebibyte comfortably holds a large legitimate result — pages of mail,
    /// a long message history — while capping the host's allocation for any one frame to a fixed
    /// ceiling. Applied as a character budget while the line is still being read, so an over-limit
    /// frame is dropped before it is ever fully held in memory; the ceiling on retained memory is
    /// therefore about twice this many bytes (a UTF-16 code unit is two bytes). A frame at exactly
    /// this size is accepted; the first character beyond it drops the frame.
    /// </remarks>
    public const int MaxPluginFrameChars = 1024 * 1024;

    /// <summary>Maximum size of a plugin observation payload, in UTF-8 bytes.</summary>
    /// <remarks>
    /// An observation is a notification — a message arrived, a gateway reconnected — not a bulk
    /// transfer, so it is capped well below a whole frame. A payload larger than this is either a
    /// defect or an attempt to flood the event bus, and is dropped rather than published (F-4).
    /// </remarks>
    public const int MaxObservationPayloadBytes = 256 * 1024;
}
