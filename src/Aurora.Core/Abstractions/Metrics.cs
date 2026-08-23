namespace Aurora.Core.Abstractions;

/// <summary>
/// A point-in-time reading of the operational counters (docs/adr/0008).
/// </summary>
/// <remarks>
/// Counters are process-lifetime and reset on restart; only <see cref="PendingApprovals"/> is a
/// true gauge read from storage. Labelled here rather than left for the reader to discover,
/// because a counter silently reset by a crash looks exactly like a quiet period.
/// </remarks>
public sealed record MetricsSnapshot(
    DateTimeOffset SinceUtc,
    IReadOnlyDictionary<string, long> ExecutionsByOutcome,
    long IdempotencyConflicts,
    long ExecutionsUnknown,
    long AuditFailures,
    long ConsentDecisions,
    long ConsentLatencyTotalMs,
    long ConsentLatencyMaxMs,
    int PendingApprovals)
{
    /// <summary>Mean consent latency, or null when nothing has been decided yet.</summary>
    public double? ConsentLatencyMeanMs =>
        ConsentDecisions == 0 ? null : (double)ConsentLatencyTotalMs / ConsentDecisions;
}

/// <summary>Operational counters for the It.3 health surface. Implementations must be thread-safe.</summary>
public interface IAuroraMetrics
{
    void ExecutionSettled(string outcome);

    void IdempotencyConflict();

    void ExecutionUnknown();

    void AuditFailure();

    /// <summary>Records how long a caller waited between requesting approval and it being decided.</summary>
    void ConsentDecided(TimeSpan latency);

    MetricsSnapshot Snapshot(int pendingApprovals);
}
