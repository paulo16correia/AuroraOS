namespace Aurora.Core.Contracts;

/// <summary>
/// An append-only, hash-chained audit record. Contains NO secrets — only a hash of the input.
/// <c>record_hash = H(previous_hash | sequence | principal | action_id | input_hash | outcome | created_at)</c>.
/// </summary>
public sealed record AuditRecord(
    long Sequence,
    string RecordId,
    string PrincipalClientId,
    string PrincipalOsUser,
    string ActionId,
    string InputHash,
    string Outcome,
    string CreatedAtUtc,
    string PreviousHash,
    string RecordHash);
