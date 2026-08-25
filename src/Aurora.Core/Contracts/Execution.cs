using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aurora.Core.Contracts;

/// <summary>An authenticated caller. Established by the transport — never asserted by the LLM.</summary>
public sealed record Principal(string ClientId, string OsUser);

/// <summary>
/// Input of <c>aurora_execute</c>. Exactly one of <see cref="Objective"/> (NL) or
/// <see cref="ActionId"/>+<see cref="Input"/> (explicit) must be present. Unknown members are rejected.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecuteRequest(
    string? Objective = null,
    string? ActionId = null,
    JsonElement? Input = null,
    string? IdempotencyKey = null);

/// <summary>How an action was resolved from the request.</summary>
public static class ResolutionVia
{
    public const string Explicit = "explicit";
    public const string Reasoner = "reasoner";
    public const string Keyword = "keyword";
    public const string Unsupported = "unsupported";
}

/// <summary>An UNTRUSTED proposal from the reasoner. The kernel validates and commits it.</summary>
public sealed record ReasonerProposal(string? ActionId, JsonElement? Input, double Confidence, string Via);

/// <summary>An action the kernel has committed to (exists in catalog, input passed schema).</summary>
public sealed record ResolvedAction(string ActionId, JsonElement Input, double Confidence, string Via);

/// <summary>Terminal status of <c>aurora_execute</c>.</summary>
public static class ExecuteStatus
{
    public const string Completed = "completed";
    public const string Denied = "denied";
    public const string Failed = "failed";
    public const string Conflict = "conflict";
    public const string Invalid = "invalid";
    public const string InProgress = "in_progress";

    /// <summary>
    /// Aurora chose to ask before acting. Not a refusal: nothing forbade the action, and nothing
    /// was reserved or run.
    /// </summary>
    public const string Asked = "asked";
}

public static class ConsentDecision
{
    public const string AutoLow = "auto_low";
    public const string Granted = "granted";
    public const string Denied = "denied";
    public const string RequiresApproval = "requires_approval";
}

public sealed record ConsentInfo(
    string Decision, string Via, string? ApprovalId = null, string? SessionId = null);

public sealed record ExecuteError(string Code, string Message, IReadOnlyList<string>? Details = null);

/// <summary>Output of <c>aurora_execute</c>.</summary>
public sealed record ExecuteResponse(
    string Status,
    ResolvedAction? Resolved = null,
    ConsentInfo? Consent = null,
    JsonElement? Result = null,
    IReadOnlyList<string>? AuditRef = null,
    ExecuteError? Error = null,
    /// <summary>
    /// The cognitive cycle this call was reasoned through, when it was dispatched rather than
    /// executed directly. It is the handle for reading back what Aurora attended to, decided,
    /// observed and concluded — so a caller is never asked to take the outcome on trust.
    /// </summary>
    string? CycleRef = null);
