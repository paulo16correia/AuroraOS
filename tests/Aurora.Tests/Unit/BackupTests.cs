using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

public sealed class BackupTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            TestTemp.Path("bk");

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static readonly byte[] Key = Enumerable.Repeat((byte)7, 32).ToArray();

    private static (SqliteAuditStore Audit, SqliteBackupService Backup, string AnchorPath) Build(
        SqliteTestDb db, string anchorDirectory)
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var anchorPath = Path.Combine(anchorDirectory, "live.anchor");
        var audit = new SqliteAuditStore(db.Factory, clock, Key, new AuditAnchorFile(anchorPath));
        var backup = new SqliteBackupService(db.Factory, clock, Key, anchorPath);
        return (audit, backup, anchorPath);
    }

    [Fact]
    public async Task Backup_ProducesAVerifiableSnapshot()
    {
        using var db = new SqliteTestDb();
        using var dir = new TempDir();
        var (audit, backup, _) = Build(db, dir.Path);

        await audit.AppendAsync(
            new AuditEntry("c1", "u1", "echo.say", "ih1", "completed"), CancellationToken.None);
        await audit.AppendAsync(
            new AuditEntry("c1", "u1", "echo.say", "ih2", "completed"), CancellationToken.None);

        BackupResult result = await backup.BackupAsync(Path.Combine(dir.Path, "out"), CancellationToken.None);

        Assert.True(File.Exists(result.DatabasePath));
        Assert.True(result.AuditVerified, result.AuditReason);
    }

    [Fact]
    public async Task Backup_CarriesTheAnchorSoCompletenessIsStillProvable()
    {
        using var db = new SqliteTestDb();
        using var dir = new TempDir();
        var (audit, backup, _) = Build(db, dir.Path);
        await audit.AppendAsync(
            new AuditEntry("c1", "u1", "echo.say", "ih1", "completed"), CancellationToken.None);

        BackupResult result = await backup.BackupAsync(Path.Combine(dir.Path, "out"), CancellationToken.None);

        Assert.True(File.Exists(result.AnchorPath));
    }

    [Fact]
    public async Task Backup_DoesNotCopyTheSigningKey()
    {
        using var db = new SqliteTestDb();
        using var dir = new TempDir();
        var (audit, backup, _) = Build(db, dir.Path);
        await audit.AppendAsync(
            new AuditEntry("c1", "u1", "echo.say", "ih1", "completed"), CancellationToken.None);

        var outDir = Path.Combine(dir.Path, "out");
        await backup.BackupAsync(outDir, CancellationToken.None);

        // Shipping the key with the database would hand whoever steals the backup everything
        // needed to rewrite the chain and re-sign it.
        Assert.Empty(Directory.GetFiles(outDir, "*.key"));

        // Compare content, not size: a SQLite WAL header is itself 32 bytes, so a size
        // heuristic would flag a sidecar as if it were the key. Sidecars are SQLite's own
        // bookkeeping rather than backup payload, and they appear and vanish while it tidies up,
        // so they are excluded instead of raced against.
        var payload = Directory.GetFiles(outDir)
            .Where(f => !f.EndsWith("-wal", StringComparison.Ordinal)
                     && !f.EndsWith("-shm", StringComparison.Ordinal)
                     && !f.EndsWith("-journal", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(payload);
        foreach (var file in payload)
        {
            Assert.NotEqual(Key, await File.ReadAllBytesAsync(file));
        }
    }

    [Fact]
    public async Task Backup_TamperedAfterwards_FailsVerification()
    {
        using var db = new SqliteTestDb();
        using var dir = new TempDir();
        var (audit, backup, _) = Build(db, dir.Path);
        await audit.AppendAsync(
            new AuditEntry("c1", "u1", "echo.say", "ih1", "completed"), CancellationToken.None);

        BackupResult result = await backup.BackupAsync(Path.Combine(dir.Path, "out"), CancellationToken.None);
        Assert.True(result.AuditVerified);

        using (var connection = new SqliteConnectionFactory(result.DatabasePath).Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE audit_record SET outcome = 'denied' WHERE sequence = 1;";
            command.ExecuteNonQuery();
        }

        AuditVerification verification =
            await backup.VerifyAsync(result.DatabasePath, result.AnchorPath, CancellationToken.None);

        Assert.False(verification.Ok);
    }

    [Fact]
    public async Task Backup_LeavesNothingOfAurorasHoldingTheFile()
    {
        using var db = new SqliteTestDb();
        using var dir = new TempDir();
        var (audit, backup, _) = Build(db, dir.Path);
        await audit.AppendAsync(
            new AuditEntry("c1", "u1", "echo.say", "ih1", "completed"), CancellationToken.None);

        BackupResult result = await backup.BackupAsync(
            Path.Combine(dir.Path, "out"), CancellationToken.None);

        // A backup is a file somebody is about to read, copy, or move to another machine, and
        // BackupAsync returning has to mean Aurora is finished with it. Microsoft.Data.Sqlite
        // pools connections, so a disposed one keeps the operating system handle open — and the
        // handle SQLite holds is a writing one. On Windows the next reader is then refused with a
        // sharing violation, which is what this asserts is not happening; on Unix there is no
        // mandatory locking and this passes either way, which is exactly why the bug survived to
        // be found on Windows (docs/adr/0076).
        Assert.NotEmpty(await File.ReadAllBytesAsync(result.DatabasePath));

        // Opened the strict way on purpose: FileShare.None fails if anybody at all still holds it,
        // where the read above only fails against a writer.
        using (File.Open(result.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
        }

        // And the proof that it is really let go of: the file can be deleted, which Windows
        // refuses outright while a handle is open.
        File.Delete(result.DatabasePath);
        Assert.False(File.Exists(result.DatabasePath));
    }

    [Fact]
    public async Task Backup_Verified_StillLeavesTheFileAlone()
    {
        // Verification opens the backup a second time, with a second connection factory. That one
        // has to let go as well, or checking a backup is what locks it.
        using var db = new SqliteTestDb();
        using var dir = new TempDir();
        var (audit, backup, _) = Build(db, dir.Path);
        await audit.AppendAsync(
            new AuditEntry("c1", "u1", "echo.say", "ih1", "completed"), CancellationToken.None);

        BackupResult result = await backup.BackupAsync(
            Path.Combine(dir.Path, "out"), CancellationToken.None);

        AuditVerification again = await backup.VerifyAsync(
            result.DatabasePath, result.AnchorPath, CancellationToken.None);

        Assert.True(again.Ok, again.Reason);

        using (File.Open(result.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
        }
    }

    [Fact]
    public async Task Backup_OfAnEmptyDatabase_Verifies()
    {
        using var db = new SqliteTestDb();
        using var dir = new TempDir();
        var (_, backup, _) = Build(db, dir.Path);

        BackupResult result = await backup.BackupAsync(Path.Combine(dir.Path, "out"), CancellationToken.None);

        Assert.True(result.AuditVerified, result.AuditReason);
    }
}
