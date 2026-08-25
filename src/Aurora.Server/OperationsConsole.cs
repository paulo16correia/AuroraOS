using System.Globalization;
using System.Security.Cryptography;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Time;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Server;

/// <summary>
/// Backup and restore-testing, from the server host's console (RFC 12 rule 3).
/// </summary>
/// <remarks>
/// Not an HTTP endpoint. A backup is a copy of everything Aurora knows, and an endpoint that
/// produced one on request would be a way to exfiltrate the instance using a credential meant for
/// something else. The console is reachable by whoever already has the machine.
/// </remarks>
public static class OperationsConsole
{
    public static bool TryHandle(string[] args, AuroraServerOptions options)
    {
        var command = args.FirstOrDefault();
        if (command is not ("backup" or "restore-test"))
        {
            return false;
        }

        var target = args.Length > 1 ? args[1] : null;
        if (string.IsNullOrWhiteSpace(target))
        {
            Console.WriteLine($"[Aurora] Usage: {command} <directory>");
            return true;
        }

        return command == "backup" ? Backup(options, target) : RestoreTest(options, target);
    }

    private static bool Backup(AuroraServerOptions options, string directory)
    {
        var service = new SqliteBackupService(
            new SqliteConnectionFactory(options.DbPath), new SystemClock(),
            AuditKeyFile.LoadOrCreate(options.AuditKeyPath), options.AuditAnchorPath);

        BackupResult result = service
            .BackupAsync(directory, CancellationToken.None).GetAwaiter().GetResult();

        BackupSnapshot snapshot = Describe(result);
        var manifestPath = result.DatabasePath + ".json";
        File.WriteAllText(manifestPath, Core.AuroraJson.Serialize(snapshot));

        Console.WriteLine($"[Aurora] {result.DatabasePath}");
        Console.WriteLine($"[Aurora] checksum {snapshot.Checksum[..16]}…");

        if (!result.AuditVerified)
        {
            // A copy whose chain does not verify is not a backup, and saying so now is the whole
            // point of verifying against the copy rather than against the original.
            Console.Error.WriteLine(
                $"[Aurora] REFUSED: the audit chain in this copy does not verify ({result.AuditReason}). "
                + "Do not rely on it.");
            return true;
        }

        Console.WriteLine("[Aurora] The audit chain in the copy verifies.");
        Console.WriteLine("[Aurora] Not yet proven to restore. Run: restore-test " + result.DatabasePath);
        return true;
    }

    /// <summary>
    /// Proves a backup restores, in a directory that is not the live one (RFC 12 rule 3).
    /// </summary>
    /// <remarks>
    /// A backup nobody has restored is a belief about a file. The isolated target is a temporary
    /// directory rather than anything configured, so a restore test cannot land on the instance it
    /// was meant to protect.
    /// </remarks>
    private static bool RestoreTest(AuroraServerOptions options, string backupPath)
    {
        if (!File.Exists(backupPath))
        {
            Console.Error.WriteLine($"[Aurora] No such backup: {backupPath}");
            return true;
        }

        var isolated = Path.Combine(
            Path.GetTempPath(), $"aurora-restore-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(isolated);

        try
        {
            var databasePath = Path.Combine(isolated, "aurora.db");
            var anchorPath = databasePath + ".anchor";

            File.Copy(backupPath, databasePath);
            if (File.Exists(backupPath + ".anchor"))
            {
                File.Copy(backupPath + ".anchor", anchorPath);
            }

            var service = new SqliteBackupService(
                new SqliteConnectionFactory(databasePath), new SystemClock(),
                AuditKeyFile.LoadOrCreate(options.AuditKeyPath), anchorPath);

            AuditVerification verified = service
                .VerifyAsync(databasePath, anchorPath, CancellationToken.None)
                .GetAwaiter().GetResult();

            var report = new RestoreReport(
                Path.GetFileName(backupPath), isolated, Restored: true, verified.Ok,
                verified.Ok
                    ? "restored into an isolated directory and the audit chain verifies"
                    : $"restored, but the audit chain broke at {verified.BrokenSequence}");

            Console.WriteLine($"[Aurora] {report.Detail}");

            if (verified.Ok)
            {
                var manifestPath = backupPath + ".json";
                if (File.Exists(manifestPath))
                {
                    BackupSnapshot snapshot = Core.AuroraJson
                        .Deserialize<BackupSnapshot>(File.ReadAllText(manifestPath));

                    File.WriteAllText(manifestPath, Core.AuroraJson.Serialize(snapshot with
                    {
                        RestoreTestedAtUtc = Iso(DateTimeOffset.UtcNow),
                        Status = BackupStatus.RestoreTested,
                    }));
                }
            }

            return true;
        }
        finally
        {
            try
            {
                Directory.Delete(isolated, recursive: true);
            }
            catch (IOException)
            {
                Console.Error.WriteLine($"[Aurora] Left behind: {isolated}");
            }
        }
    }

    private static BackupSnapshot Describe(BackupResult result)
    {
        using FileStream stream = File.OpenRead(result.DatabasePath);
        var checksum = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var now = Iso(DateTimeOffset.UtcNow);

        return new BackupSnapshot(
            Path.GetFileNameWithoutExtension(result.DatabasePath),
            Scope: "instance",
            LocationRef: result.DatabasePath,
            StartedAtUtc: now,
            CompletedAtUtc: now,
            checksum,
            RestoreTestedAtUtc: null,
            RetentionUntilUtc: Iso(DateTimeOffset.UtcNow.AddDays(90)),

            // COMPLETE, not RESTORE_TESTED. Those are different claims and only the second one is
            // worth anything when the original is gone.
            Status: result.AuditVerified ? BackupStatus.Complete : BackupStatus.Failed,
            result.AuditVerified);
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
