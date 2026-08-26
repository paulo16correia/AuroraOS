using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>One reading of the host.</summary>
/// <remarks>
/// Null means the platform could not report it. Kept distinct from zero all the way through,
/// because "I could not measure it" and "there is none in use" call for opposite responses.
/// </remarks>
/// <param name="DiskFreeBytes">
/// How much room is actually left, which is a different question from how full the disk is.
/// </param>
/// <remarks>
/// A fraction answers "how much of this machine is spoken for". What Aurora needs to know is
/// whether there is room to write a database, a snapshot and a backup — and 3% of a 228 GB disk
/// is nearly seven gigabytes, while 3% of a 32 GB disk is one. Both read as 97% used, and only
/// one of them is a problem. Reported alongside the fraction rather than instead of it: the
/// fraction is still the right way to say how full a disk is, it is just not the right way to
/// say whether Aurora can work.
/// </remarks>
public sealed record ResourceReading(
    double? CpuFraction,
    double? MemoryFraction,
    double? DiskFraction,
    long? DiskFreeBytes = null);

/// <summary>
/// Reads the host's actual load.
/// </summary>
/// <remarks>
/// A seam, because this is the one part of the resource model that cannot be deterministic: it
/// reports whatever the machine happens to be doing. Behind an interface, the policy above it —
/// what counts as constrained, what gives way first — can be tested against stated conditions
/// instead of against whatever the test machine's disk looked like that afternoon.
/// </remarks>
public interface IResourceProbe
{
    ResourceReading Read();
}

/// <summary>
/// What Aurora actually has to work with (RFC 033).
/// </summary>
/// <remarks>
/// The point of this model is that priority depends on real capacity rather than on a blind queue,
/// so that Aurora does not become least reliable exactly when there is most going on.
/// </remarks>
public interface IResourceModel
{
    /// <summary>
    /// Reads the current state. Metrics this platform cannot report come back as unmeasured rather
    /// than as zero, because a missing reading is not a healthy one.
    /// </summary>
    Task<ResourceState> ObserveAsync(CancellationToken ct);

    /// <summary>
    /// Decides whether a piece of work can start now (rule 2: nothing is treated as unlimited).
    /// </summary>
    /// <remarks>
    /// This is a capacity question and only a capacity question. ALLOW here is not permission to
    /// act — policy, consent and approval are decided elsewhere and are not softened by there
    /// being room.
    /// </remarks>
    Task<AdmissionResult> AdmitAsync(
        string workRef, string workClass, double estimatedCost, ResourceBudget budget, CancellationToken ct);

    /// <summary>Gives capacity back, whatever the work's outcome was.</summary>
    Task<ResourceState> ReleaseAsync(string reservationId, string outcome, CancellationToken ct);

    /// <summary>What is currently held.</summary>
    IReadOnlyList<Reservation> Held { get; }
}
