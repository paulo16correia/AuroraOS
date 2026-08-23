using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// Outcome of an integrity check. <paramref name="Reason"/> distinguishes an edited record
/// from a truncated tail, which the chain alone cannot tell apart.
/// </summary>
public sealed record AuditVerification(bool Ok, long? BrokenSequence, string? Reason = null);

/// <summary>
/// One audit record. Beyond the bare outcome it carries *why* the kernel decided as it did
/// (design/0006): with an untrusted reasoner able to pick the action since It.1, "what happened"
/// is no longer enough to reconstruct a decision after the fact.
/// </summary>
public sealed record AuditEntry(
    string PrincipalClientId,
    string PrincipalWindowsUser,
    string ActionId,
    string InputHash,
    string Outcome,
    string? Risk = null,
    string? Via = null,
    string? Decision = null,
    string? PolicyIds = null,
    string? Reason = null);

/// <summary>Append-only, hash-chained audit log. Integrity failure is fail-closed.</summary>
public interface IAuditStore
{
    /// <summary>Appends a record and returns its record_hash.</summary>
    Task<string> AppendAsync(AuditEntry entry, CancellationToken ct);

    /// <summary>Recomputes the chain and reports the first break, if any.</summary>
    Task<AuditVerification> VerifyChainAsync(CancellationToken ct);
}

/// <summary>Idempotency ledger keyed by (principal client, idempotency_key).</summary>
public interface IIdempotencyStore
{
    Task<IdempotencyBegin> BeginAsync(Principal principal, string key, string requestHash, CancellationToken ct);

    /// <summary>
    /// Transitions ACCEPTED→EXECUTING. Returns false when the row is not in ACCEPTED state (the
    /// reservation is no longer owned by this caller); the caller must then fail closed.
    /// </summary>
    Task<bool> MarkExecutingAsync(Principal principal, string key, CancellationToken ct);

    Task CompleteAsync(Principal principal, string key, string state, string resultJson, CancellationToken ct);

    /// <summary>
    /// Releases a reservation that is still ACCEPTED (not yet executed), so a later call with the
    /// same key starts a fresh reservation instead of replaying this attempt's disposition forever.
    /// A no-op, not an error, when the row is no longer ACCEPTED.
    /// </summary>
    Task AbandonAsync(Principal principal, string key, CancellationToken ct);
}
