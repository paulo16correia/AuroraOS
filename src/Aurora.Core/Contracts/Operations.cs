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
/// One component's answer to "are you working" (RFC 12).
/// </summary>
/// <remarks>
/// <see cref="DetailSafe"/> is named that way in the RFC and the name is the rule: a health
/// endpoint is the most-scraped surface a system has, so what it says must be safe to read
/// without authorization even when it is served behind one. Counts and states, never content.
/// </remarks>
public sealed record HealthCheck(
    string Component,
    string Status,
    string CheckedAtUtc,
    long LatencyMs,
    IReadOnlyList<string> DependencyRefs,
    string DetailSafe);

/// <summary>
/// What a release consists of, fixed before it is deployed (RFC 12 rule 2).
/// </summary>
/// <remarks>
/// Image <i>digests</i> rather than tags, because a tag can be moved and a digest cannot: pinning
/// a tag pins a name, not a build. <see cref="RollbackReleaseId"/> is required rather than
/// optional — a release with nowhere to go back to is not reversible, and rule 2 asks for
/// reversibility rather than for hope.
/// </remarks>
public sealed record DeploymentManifest(
    string ReleaseId,
    IReadOnlyList<string> ImageDigests,
    int SchemaVersion,
    string ConfigVersion,
    IReadOnlyList<int> MigrationIds,
    string ApprovedBy,
    string DeployedAtUtc,
    string RollbackReleaseId);

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
