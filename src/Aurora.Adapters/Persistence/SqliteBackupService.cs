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
        await using (var destination = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()))
        {
            await destination.OpenAsync(ct).ConfigureAwait(false);
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
        var factory = new SqliteConnectionFactory(databasePath);
        var store = new SqliteAuditStore(factory, _clock, _auditKey, new AuditAnchorFile(anchorPath));
        return await store.VerifyChainAsync(ct).ConfigureAwait(false);
    }
}
