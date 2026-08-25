namespace Aurora.Core.Contracts;

public static class HealthStatus
{
    public const string Pass = "PASS";

    /// <summary>Working, and something about it needs attention before it stops working.</summary>
    public const string Warn = "WARN";

    public const string Fail = "FAIL";

    /// <summary>The worst of a set, which is what the whole system's status is.</summary>
    public static string Worst(IEnumerable<string> statuses)
    {
        var worst = Pass;
        foreach (var status in statuses)
        {
            if (status == Fail)
            {
                return Fail;
            }

            if (status == Warn)
            {
                worst = Warn;
            }
        }

        return worst;
    }
}

/// <summary>
/// One component's answer to "are you working" (docs/adr/0045).
/// </summary>
/// <remarks>
/// <see cref="DetailSafe"/> is the rule rather than a label: a health surface is the one most
/// likely to be read by something that should not see anything, so what it says is counts and
/// states and never content.
/// </remarks>
public sealed record HealthCheck(
    string Component,
    string Status,
    string CheckedAtUtc,
    long LatencyMs,
    IReadOnlyList<string> DependencyRefs,
    string DetailSafe);

public static class BackupStatus
{
    public const string Running = "RUNNING";
    public const string Complete = "COMPLETE";
    public const string Failed = "FAILED";

    /// <summary>Written, and proven to restore into an isolated target. The only status that counts.</summary>
    public const string RestoreTested = "RESTORE_TESTED";
}

public sealed record BackupSnapshot(
    string Id,
    string Scope,
    string LocationRef,
    string StartedAtUtc,
    string? CompletedAtUtc,
    string Checksum,
    string? RestoreTestedAtUtc,
    string? RetentionUntilUtc,
    string Status,
    /// <summary>Whether the audit chain in the copy verifies. A backup that does not is not one.</summary>
    bool AuditVerified);

public sealed record RestoreReport(
    string SnapshotId,
    string IsolatedTarget,
    bool Restored,
    bool AuditVerified,
    string Detail);
