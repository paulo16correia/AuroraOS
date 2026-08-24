namespace Aurora.Core.Contracts;

/// <summary>Lifecycle of a vault item (RFC 040).</summary>
/// <remarks>
/// RFC 09 lists <c>ACTIVE|REVOKED|EXPIRED</c> for <c>SecretReference</c> while RFC 040 gives the
/// aggregate <c>ACTIVE → ROTATING → REVOKED|EXPIRED</c>. The Domain Model is the authority on
/// aggregates, so its superset is used; a store that never rotates simply never sees ROTATING.
/// </remarks>
public static class VaultItemStatus
{
    public const string Active = "ACTIVE";
    public const string Rotating = "ROTATING";
    public const string Revoked = "REVOKED";
    public const string Expired = "EXPIRED";
}

/// <summary>
/// A pointer to a secret. Carries everything needed to decide whether a lease is allowed and
/// nothing that would reveal the value (RFC 09).
/// </summary>
public sealed record SecretReference(
    string Id,
    string Provider,
    string Locator,
    string Purpose,
    IReadOnlyList<string> AllowedToolIds,
    string? RotationDueAtUtc,
    string Status);

/// <summary>The tool invocation a lease is being requested for.</summary>
/// <remarks>
/// RFC 09 names the lease argument <c>tool_call_id</c>; the tool id travels with it because
/// <see cref="SecretReference.AllowedToolIds"/> cannot be enforced without knowing which tool is
/// asking.
/// </remarks>
public sealed record ToolCallRef(string ToolCallId, string ToolId);
