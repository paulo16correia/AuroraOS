using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// What Aurora knows about itself (RFC 027).
/// </summary>
/// <remarks>
/// An agent that does not know what it can do makes promises it cannot keep and attempts that were
/// never going to work. Self is what lets Aurora say "I can prepare this, but not send it" on the
/// strength of something observed rather than something assumed.
/// </remarks>
public interface ISelfModel
{
    /// <summary>Takes a fresh reading and stores it as the next version.</summary>
    Task<SelfModel> RefreshAsync(string mindId, CancellationToken ct);

    /// <summary>
    /// What Aurora will say about itself. Never secrets, topology or credential identifiers.
    /// </summary>
    Task<SafeSelfDescription> DescribeAsync(MemoryAccessContext access, CancellationToken ct);

    /// <summary>
    /// Whether one capability is installed, permitted, and safe to use right now — separately.
    /// </summary>
    /// <remarks>
    /// Rule 2. Three questions, three answers, and none of them implies another.
    /// </remarks>
    Task<CapabilityAssessment> CanAsync(
        string actionId, Principal principal, CancellationToken ct);

    /// <summary>Stops Aurora starting work of its own, with a reason and an actor.</summary>
    Task<SelfModel> PauseAsync(string actor, string reason, CancellationToken ct);

    Task<SelfModel> ResumeAsync(string actor, CancellationToken ct);

    /// <summary>The current reading, or null if nothing has been observed yet.</summary>
    Task<SelfModel?> CurrentAsync(CancellationToken ct);
}
