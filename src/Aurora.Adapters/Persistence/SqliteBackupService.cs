using System.Globalization;
using Aurora.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Persistence;

/// <summary>Where a backup landed and whether its audit chain still verified.</summary>
public sealed record BackupResult(string DatabasePath, string AnchorPath, bool AuditVerified, string? AuditReason);

/// <summary>
/// Online backup of the Aurora database plus its audit anchor (docs/adr/0009).
/// </summary>
/// <remarks>
/// Uses SQLite's own backup API rather than copying the file: a plain copy of a WAL database while
/// writers are active can capture a torn state that only fails much later, at restore time.
/// <para>
/// The audit signing key is deliberately NOT copied. Keeping the key beside the database in the
/// same backup would hand an attacker who steals that backup everything needed to rewrite the
/// chain and re-sign it — which is exactly the defence docs/adr/0005 set out to build. The key is
/// the operator's to back up separately, and to store somewhere the database backups do not reach.
/// </para>
/// </remarks>
public sealed class SqliteBackupService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly IClock _clock;
    private readonly byte[] _auditKey;
    private readonly string _anchorPath;

    public SqliteBackupService(
        SqliteConnectionFactory factory, IClock clock, byte[] auditKey, string anchorPath)
    {
        _factory = factory;
        _clock = clock;
        _auditKey = auditKey;
        _anchorPath = anchorPath;
    }

    /// <summary>
    /// Writes a consistent snapshot into <paramref name="destinationDirectory"/> and verifies the
    /// copy before reporting success.
    /// </summary>
    /// <remarks>
    /// Verification runs against the backup, not the live database. A backup whose chain does not
    /// verify is worthless, and the moment to discover that is now — not during a restore, when
    /// the original may already be gone.
    /// </remarks>
    public async Task<BackupResult> BackupAsync(string destinationDirectory, CancellationToken ct)
    {
        Directory.CreateDirectory(destinationDirectory);

        var stamp = _clock.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var databasePath = Path.Combine(destinationDirectory, $"aurora-{stamp}.db");
        var anchorPath = databasePath + ".anchor";

        await using (SqliteConnection source = await _factory.OpenAsync(ct).ConfigureAwait(false))
        await using (SqliteConnection destination =
            await Snapshot(databasePath).OpenAsync(ct).ConfigureAwait(false))
        {
            source.BackupDatabase(destination);
        }

        // The anchor travels with the snapshot; without it a restored database cannot be shown to
        // be complete, only internally consistent.
        if (File.Exists(_anchorPath))
        {
            File.Copy(_anchorPath, anchorPath, overwrite: true);
        }

        AuditVerification verification = await VerifyAsync(databasePath, anchorPath, ct).ConfigureAwait(false);

        return new BackupResult(databasePath, anchorPath, verification.Ok, verification.Reason);
    }

    /// <summary>Verifies an existing backup with the current signing key.</summary>
    public async Task<AuditVerification> VerifyAsync(string databasePath, string anchorPath, CancellationToken ct)
    {
        var store = new SqliteAuditStore(
            Snapshot(databasePath), _clock, _auditKey, new AuditAnchorFile(anchorPath));

        return await store.VerifyChainAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A connection factory for a backup file, which Aurora opens, finishes with, and lets go of.
    /// </summary>
    /// <remarks>
    /// Unpooled, which is the whole point of it existing. A backup is a file somebody is about to
    /// read, copy, or hand to another machine, and this method returning leaves nothing of Aurora's
    /// holding it open. Pooled, the handle survives every <c>Dispose</c> until the pool is cleared
    /// — and on Windows a reader then gets "the process cannot access the file because it is being
    /// used by another process", from a backup Aurora had already reported as complete
    /// (docs/adr/0076).
    /// </remarks>
    private static SqliteConnectionFactory Snapshot(string databasePath) =>
        new(databasePath, pooled: false);
}
