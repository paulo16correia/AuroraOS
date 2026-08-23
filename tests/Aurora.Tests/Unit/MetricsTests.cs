using Aurora.Adapters.Observability;
using Aurora.Core.Abstractions;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

public sealed class MetricsTests
{
    private static InMemoryMetrics New() => new(new TestClock(DateTimeOffset.UnixEpoch));

    [Fact]
    public void Snapshot_StartsEmpty()
    {
        MetricsSnapshot snapshot = New().Snapshot(pendingApprovals: 0);

        Assert.Empty(snapshot.ExecutionsByOutcome);
        Assert.Equal(0, snapshot.IdempotencyConflicts);
        Assert.Null(snapshot.ConsentLatencyMeanMs);
    }

    [Fact]
    public void ExecutionsAreCountedPerOutcome()
    {
        var metrics = New();
        metrics.ExecutionSettled("completed");
        metrics.ExecutionSettled("completed");
        metrics.ExecutionSettled("policy_denied");

        MetricsSnapshot snapshot = metrics.Snapshot(0);

        Assert.Equal(2, snapshot.ExecutionsByOutcome["completed"]);
        Assert.Equal(1, snapshot.ExecutionsByOutcome["policy_denied"]);
    }

    [Fact]
    public void ConsentLatency_TracksMeanAndMax()
    {
        var metrics = New();
        metrics.ConsentDecided(TimeSpan.FromMilliseconds(100));
        metrics.ConsentDecided(TimeSpan.FromMilliseconds(300));

        MetricsSnapshot snapshot = metrics.Snapshot(0);

        Assert.Equal(2, snapshot.ConsentDecisions);
        Assert.Equal(200, snapshot.ConsentLatencyMeanMs);
        Assert.Equal(300, snapshot.ConsentLatencyMaxMs);
    }

    [Fact]
    public void ConsentLatency_MaxSurvivesALaterSmallerValue()
    {
        var metrics = New();
        metrics.ConsentDecided(TimeSpan.FromMilliseconds(500));
        metrics.ConsentDecided(TimeSpan.FromMilliseconds(10));

        Assert.Equal(500, metrics.Snapshot(0).ConsentLatencyMaxMs);
    }

    [Fact]
    public void NegativeLatency_IsClampedRatherThanSkewingTheMean()
    {
        // Clock skew across a restart can produce a decision that appears to precede its request.
        var metrics = New();
        metrics.ConsentDecided(TimeSpan.FromMilliseconds(-5000));

        Assert.Equal(0, metrics.Snapshot(0).ConsentLatencyTotalMs);
    }

    [Fact]
    public void CountersAreIndependent()
    {
        var metrics = New();
        metrics.IdempotencyConflict();
        metrics.ExecutionUnknown();
        metrics.ExecutionUnknown();
        metrics.AuditFailure();

        MetricsSnapshot snapshot = metrics.Snapshot(7);

        Assert.Equal(1, snapshot.IdempotencyConflicts);
        Assert.Equal(2, snapshot.ExecutionsUnknown);
        Assert.Equal(1, snapshot.AuditFailures);
        Assert.Equal(7, snapshot.PendingApprovals);
    }

    [Fact]
    public async Task ConcurrentUpdates_AreNotLost()
    {
        var metrics = New();

        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() =>
        {
            metrics.ExecutionSettled("completed");
            metrics.IdempotencyConflict();
        })));

        MetricsSnapshot snapshot = metrics.Snapshot(0);

        Assert.Equal(200, snapshot.ExecutionsByOutcome["completed"]);
        Assert.Equal(200, snapshot.IdempotencyConflicts);
    }
}
