using Aurora.Adapters.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aurora.Tests.Unit;

public sealed class SchemaMigrationTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"aurora-mig-{Guid.NewGuid():N}.db");

    private SqliteConnectionFactory Factory => new(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                File.Delete(_path + suffix);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string? ColumnOf(SqliteConnectionFactory factory, string table, string column)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}') WHERE name = @c;";
        command.Parameters.AddWithValue("@c", column);
        return command.ExecuteScalar() as string;
    }

    private static int Version(SqliteConnectionFactory factory)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_version LIMIT 1;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    [Fact]
    public void AFreshDatabase_IsStampedAtTheCurrentVersion()
    {
        new SqliteDatabase(Factory).Initialize();

        Assert.Equal(SqliteDatabase.TargetSchemaVersion, Version(Factory));
        Assert.Equal("principal_os_user", ColumnOf(Factory, "audit_record", "principal_os_user"));
    }

    [Fact]
    public void Initialize_IsIdempotent()
    {
        new SqliteDatabase(Factory).Initialize();
        new SqliteDatabase(Factory).Initialize();

        Assert.Equal(SqliteDatabase.TargetSchemaVersion, Version(Factory));
    }

    [Fact]
    public void AV1Database_IsMigratedAndKeepsItsRows()
    {
        // Build a database in the shape this repository shipped before the rename.
        using (var connection = Factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE schema_version (version INTEGER NOT NULL);
                INSERT INTO schema_version (version) VALUES (1);

                CREATE TABLE audit_record (
                  sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                  record_id TEXT NOT NULL, principal_client_id TEXT NOT NULL,
                  principal_windows_user TEXT NOT NULL, action_id TEXT NOT NULL,
                  input_hash TEXT NOT NULL, outcome TEXT NOT NULL, created_at_utc TEXT NOT NULL,
                  previous_hash TEXT NOT NULL, record_hash TEXT NOT NULL,
                  risk TEXT NULL, via TEXT NULL, decision TEXT NULL,
                  policy_ids TEXT NULL, reason TEXT NULL);

                CREATE TABLE approval (
                  approval_id TEXT PRIMARY KEY, principal_client_id TEXT NOT NULL,
                  principal_windows_user TEXT NOT NULL, action_id TEXT NOT NULL,
                  scope_hash TEXT NOT NULL, status TEXT NOT NULL, created_at_utc TEXT NOT NULL,
                  expires_at_utc TEXT NOT NULL, decided_at_utc TEXT NULL);

                CREATE TABLE consent_session (
                  session_id TEXT PRIMARY KEY, principal_client_id TEXT NOT NULL,
                  principal_windows_user TEXT NOT NULL, server_boot_id TEXT NOT NULL,
                  policy_version TEXT NOT NULL, status TEXT NOT NULL, actions_used INTEGER NOT NULL,
                  max_actions INTEGER NOT NULL, created_at_utc TEXT NOT NULL,
                  expires_at_utc TEXT NOT NULL);

                INSERT INTO audit_record
                  (record_id, principal_client_id, principal_windows_user, action_id, input_hash,
                   outcome, created_at_utc, previous_hash, record_hash)
                VALUES ('r1', 'c1', 'someone', 'echo.say', 'ih', 'completed', 'then', '', 'h1');
                """;
            command.ExecuteNonQuery();
        }

        new SqliteDatabase(Factory).Initialize();

        Assert.Equal(SqliteDatabase.TargetSchemaVersion, Version(Factory));
        Assert.Equal("principal_os_user", ColumnOf(Factory, "audit_record", "principal_os_user"));
        Assert.Null(ColumnOf(Factory, "audit_record", "principal_windows_user"));

        // A rename must not disturb the values, or the audit chain would stop verifying.
        using var check = Factory.Open();
        using var read = check.CreateCommand();
        read.CommandText = "SELECT principal_os_user FROM audit_record WHERE record_id = 'r1';";
        Assert.Equal("someone", read.ExecuteScalar());
    }
}
