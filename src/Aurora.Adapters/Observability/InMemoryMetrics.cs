using System.Collections.Concurrent;
using Aurora.Core.Abstractions;

namespace Aurora.Adapters.Observability;

/// <summary>
/// Thread-safe, in-process counters (docs/adr/0008). Deliberately not a time series: the goal is to
/// answer "is this install healthy right now", not to replace a monitoring system.
/// </summary>
public sealed class InMemoryMetrics : IAuroraMetrics
{
    private readonly ConcurrentDictionary<string, long> _executionsByOutcome = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _since;

    private long _idempotencyConflicts;
    private long _executionsUnknown;
    private long _auditFailures;
    private long _consentDecisions;
    private long _consentLatencyTotalMs;
    private long _consentLatencyMaxMs;

    public InMemoryMetrics(IClock clock) => _since = clock.UtcNow;

    public void ExecutionSettled(string outcome) =>
        _executionsByOutcome.AddOrUpdate(outcome, 1, static (_, current) => current + 1);

    public void IdempotencyConflict() => Interlocked.Increment(ref _idempotencyConflicts);

    public void ExecutionUnknown() => Interlocked.Increment(ref _executionsUnknown);

    public void AuditFailure() => Interlocked.Increment(ref _auditFailures);

    public void ConsentDecided(TimeSpan latency)
    {
        var ms = (long)Math.Max(0, latency.TotalMilliseconds);
        Interlocked.Increment(ref _consentDecisions);
        Interlocked.Add(ref _consentLatencyTotalMs, ms);

        // Compare-and-swap loop: Interlocked has no atomic "max".
        long observed = Interlocked.Read(ref _consentLatencyMaxMs);
        while (ms > observed)
        {
            var previous = Interlocked.CompareExchange(ref _consentLatencyMaxMs, ms, observed);
            if (previous == observed)
            {
                break;
            }

            observed = previous;
        }
    }

    public MetricsSnapshot Snapshot(int pendingApprovals) => new(
        _since,
        new Dictionary<string, long>(_executionsByOutcome, StringComparer.Ordinal),
        Interlocked.Read(ref _idempotencyConflicts),
        Interlocked.Read(ref _executionsUnknown),
        Interlocked.Read(ref _auditFailures),
        Interlocked.Read(ref _consentDecisions),
        Interlocked.Read(ref _consentLatencyTotalMs),
        Interlocked.Read(ref _consentLatencyMaxMs),
        pendingApprovals);
}
