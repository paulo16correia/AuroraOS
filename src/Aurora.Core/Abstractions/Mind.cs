using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// The Mind aggregate, and the only way its own state changes (RFC 020).
/// </summary>
/// <remarks>
/// RFC 010 rule 1 says no external module writes directly to Mind and sends a validated change set
/// instead. Aurora enforced the spirit of that through LAW-001 — nothing is persisted without
/// provenance — and had no change set. So the discipline existed for memories and beliefs, which
/// have their own guarded write paths, and not for the Mind's own fields, which had no write path
/// at all because there was no Mind.
/// </remarks>
public interface IMindService
{
    /// <summary>Opens the Mind for a tenant, creating it the first time.</summary>
    Task<Mind> OpenAsync(string tenantId, CancellationToken ct);

    Task<Mind?> GetAsync(string mindId, CancellationToken ct);

    /// <summary>
    /// Records a proposal. Nothing changes yet, and a proposal is not an authorization.
    /// </summary>
    Task<MindChangeSet> ProposeAsync(MindChangeSet draft, CancellationToken ct);

    /// <summary>
    /// Checks a proposal against what it must carry, and marks it VALIDATED or REJECTED.
    /// </summary>
    Task<MindChangeSet> ValidateAsync(string changeSetId, CancellationToken ct);

    /// <summary>
    /// Applies a validated change set, all of it or none of it (rule 2).
    /// </summary>
    Task<Mind> ApplyAsync(string changeSetId, CancellationToken ct);

    Task<MindChangeSet?> ChangeSetAsync(string changeSetId, CancellationToken ct);

    /// <summary>
    /// Stops the Mind starting anything of its own. Inspection and authorized export continue.
    /// </summary>
    Task<Mind> PauseAsync(string mindId, string actor, string reason, CancellationToken ct);

    Task<Mind> ResumeAsync(string mindId, string actor, CancellationToken ct);
}
