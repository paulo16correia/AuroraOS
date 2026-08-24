using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>Who is asking for an export, and what they may see (RFC 043 rule 4).</summary>
public sealed record ExportAccessContext(string Requester, bool IncludeWorkingMemory);

/// <summary>Serialisation, verification and recovery of a Mind (RFC 043).</summary>
public interface IMindStateService
{
    /// <summary>
    /// Captures a snapshot. Under <see cref="ConsistencyLevel.Strict"/> a component without a
    /// version fails the capture; under best-effort it is captured and named, because the rule is
    /// to declare non-consistency and never to pretend atomicity that does not exist.
    /// </summary>
    Task<MindStateSnapshot> CaptureAsync(
        string mindId, MindStateComponents components, ConsistencyLevel level, CancellationToken ct);

    /// <summary>Decrypts and authenticates the snapshot, marking it VERIFIED or CORRUPT.</summary>
    Task<VerificationReport> VerifyAsync(string snapshotId, CancellationToken ct);

    /// <summary>
    /// Plans a restore. Refuses a CORRUPT snapshot, moves the instance to RECOVERING, revokes
    /// temporary leases, and waits on reconciliation while any tool call is indeterminate.
    /// </summary>
    Task<RecoveryPlan> RestoreAsync(
        string snapshotId, string targetEnvironment, string instanceId, CancellationToken ct);

    /// <summary>The newest snapshot that verified, which is what a corrupt one falls back to.</summary>
    Task<MindStateSnapshot?> LastVerifiedAsync(string mindId, CancellationToken ct);

    /// <summary>Exports for a person. Vault data is never included.</summary>
    Task<RedactedExport> ExportAsync(string snapshotId, ExportAccessContext access, CancellationToken ct);

    Task<MindStateSnapshot?> GetAsync(string snapshotId, CancellationToken ct);
}
