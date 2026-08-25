using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// Outcome of an integrity check. <paramref name="Reason"/> distinguishes an edited record
/// from a truncated tail, which the chain alone cannot tell apart.
/// </summary>
public sealed record AuditVerification(
    bool Ok,
    long? BrokenSequence,
    string? Reason = null,
    /// <summary>
    /// Where an acknowledged discontinuity sits, if there is one.
    /// </summary>
    /// <remarks>
    /// Records before this point cannot be verified and never will be — the key that signed them
    /// is gone. The chain from here forward is sound. Reported rather than hidden: the honest
    /// answer to "does the audit log verify" is sometimes "from here onwards".
    /// </remarks>
    long? AcknowledgedBreakAt = null);

/// <summary>
/// One audit record. Beyond the bare outcome it carries *why* the kernel decided as it did
/// (docs/adr/0006): with an untrusted reasoner able to pick the action since It.1, "what happened"
/// is no longer enough to reconstruct a decision after the fact.
/// </summary>
public sealed record AuditEntry(
    string PrincipalClientId,
    string PrincipalOsUser,
    string ActionId,
    string InputHash,
    string Outcome,
    string? Risk = null,
    string? Via = null,
    string? Decision = null,
    string? PolicyIds = null,
    string? Reason = null);

/// <summary>One journal row as read back, with its position so a client can page.</summary>
public sealed record AuditRecordView(
    long Sequence,
    string RecordId,
    string PrincipalClientId,
    string ActionId,
    string Outcome,
    string CreatedAtUtc,
    string RecordHash,
    string? Risk,
    string? Via,
    string? Decision,
    string? PolicyIds,
    string? Reason);

/// <summary>Append-only, hash-chained audit log. Integrity failure is fail-closed.</summary>
public interface IAuditStore
{
    /// <summary>Appends a record and returns its record_hash.</summary>
    Task<string> AppendAsync(AuditEntry entry, CancellationToken ct);

    /// <summary>Recomputes the chain and reports the first break, if any.</summary>
    Task<AuditVerification> VerifyChainAsync(CancellationToken ct);

    /// <summary>
    /// Records that the chain before this point can no longer be verified, and starts a new one.
    /// </summary>
    /// <remarks>
    /// The recovery path for a lost signing key, which otherwise leaves Aurora unable to start and
    /// with no way forward. It does not repair anything and does not pretend to: the older records
    /// stay exactly as they are, permanently unverifiable, and the marker says so in the log.
    /// <para>
    /// The marker is itself signed with the current key, so someone who can write to the database
    /// but does not hold the key cannot forge a break to make a tampered log verify.
    /// </para>
    /// </remarks>
    Task<string> SealBreakAsync(string reason, string actor, CancellationToken ct);

    /// <summary>
    /// The newest record hash, or null on an empty log. A Mind State snapshot pins it so a
    /// restore can tell which audit position the snapshot belongs to (RFC 043).
    /// </summary>
    Task<string?> HeadHashAsync(CancellationToken ct);

    /// <summary>
    /// Reads the journal forward from a cursor, for the auditable query surface (RFC 10).
    /// Returns records, never secrets: the journal holds hashes and outcomes by design.
    /// </summary>
    Task<IReadOnlyList<AuditRecordView>> QueryAsync(long afterSequence, int limit, CancellationToken ct);
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

    /// <summary>
    /// Moves reservations left in EXECUTING for longer than <paramref name="staleAfter"/> to
    /// UNKNOWN, and returns how many were moved (docs/adr/0007). A process that dies mid-effect
    /// leaves EXECUTING behind; without this the key is wedged forever, because EXECUTING is
    /// deliberately not retryable — the effect may have happened.
    /// </summary>
    Task<int> ReconcileStaleAsync(TimeSpan staleAfter, CancellationToken ct);

    /// <summary>
    /// Keys whose outcome is indeterminate. RFC 043 rule 2 requires these to be reconciled
    /// before an instance acts again after a restore.
    /// </summary>
    Task<IReadOnlyList<string>> ListUnknownAsync(CancellationToken ct);
}
