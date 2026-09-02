using Aurora.Adapters.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;
using Aurora.Tests.Support;

namespace Aurora.Tests.Unit;

public sealed class SchemaMigrationTests : IDisposable
{
    private readonly string _path =
        TestTemp.Path("mig") + ".db";

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

        // A table added by a later migration, asserted on an upgraded database rather than only on
        // a fresh one. The two paths differ, and the operator's voice stop depends on this one.
        Assert.Equal("session_id", ColumnOf(Factory, "voice_session", "session_id"));

        // A rename must not disturb the values, or the audit chain would stop verifying.
        using var check = Factory.Open();
        using var read = check.CreateCommand();
        read.CommandText = "SELECT principal_os_user FROM audit_record WHERE record_id = 'r1';";
        Assert.Equal("someone", read.ExecuteScalar());

        // And every later migration ran too, not only the one this test was written for.
        Assert.True(TableExists(Factory, "schedule"));
        Assert.True(TableExists(Factory, "schedule_run"));
    }

    private static bool TableExists(SqliteConnectionFactory factory, string table)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;";
        command.Parameters.AddWithValue("@name", table);

        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    [Fact]
    public void ADatabaseWrittenBeforeTheEvaluatorGainsItsColumnsAndKeepsItsRows()
    {
        // A learning_proposal table in the shape that shipped up to schema 14: the three fields
        // RFC 08 names — expected_benefit, risk, evidence_refs — were never there.
        using (var connection = Factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE schema_version (version INTEGER NOT NULL);
                INSERT INTO schema_version (version) VALUES (14);

                CREATE TABLE learning_proposal (
                  id TEXT PRIMARY KEY, reflection_id TEXT NOT NULL, type TEXT NOT NULL,
                  change_set_json TEXT NOT NULL, evaluation_plan TEXT NOT NULL,
                  rollback_plan TEXT NOT NULL, state TEXT NOT NULL);

                INSERT INTO learning_proposal
                  (id, reflection_id, type, change_set_json, evaluation_plan, rollback_plan, state)
                VALUES ('p1', 'r1', 'MEMORY', '{}', 'plan', 'undo', 'PROPOSED');
                """;
            command.ExecuteNonQuery();
        }

        new SqliteDatabase(Factory).Initialize();

        Assert.Equal(SqliteDatabase.TargetSchemaVersion, Version(Factory));
        Assert.Equal("risk", ColumnOf(Factory, "learning_proposal", "risk"));
        Assert.Equal("evidence_refs", ColumnOf(Factory, "learning_proposal", "evidence_refs"));

        using var check = Factory.Open();
        using var read = check.CreateCommand();

        // The row survives, and the proposal it describes reads as HIGH risk rather than as low —
        // a change written before anybody recorded its risk is not thereby a safe one.
        read.CommandText = "SELECT risk, state FROM learning_proposal WHERE id = 'p1';";
        using var reader = read.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("HIGH", reader.GetString(0));
        Assert.Equal("PROPOSED", reader.GetString(1));
    }


    [Fact]
    public void ADatabaseWrittenBeforeTheConstitutionGainsItsColumns()
    {
        using (var connection = Factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE schema_version (version INTEGER NOT NULL);
                INSERT INTO schema_version (version) VALUES (15);

                CREATE TABLE decision (
                  id TEXT PRIMARY KEY, cycle_id TEXT NOT NULL, mode TEXT NOT NULL,
                  objective_ref TEXT NULL, selected_option_json TEXT NOT NULL,
                  alternatives_json TEXT NOT NULL, evidence_refs TEXT NOT NULL,
                  uncertainty TEXT NOT NULL, risk_level TEXT NOT NULL, confidence REAL NOT NULL,
                  policy_decision_ids TEXT NOT NULL, approval_required INTEGER NOT NULL,
                  expiry_at_utc TEXT NULL, status TEXT NOT NULL);

                CREATE TABLE life_episode (
                  id TEXT PRIMARY KEY, mind_id TEXT NOT NULL, kind TEXT NOT NULL,
                  occurred_at_utc TEXT NOT NULL, occurred_until_utc TEXT NULL, title TEXT NOT NULL,
                  narrative_summary TEXT NOT NULL, evidence_refs TEXT NOT NULL,
                  significance TEXT NOT NULL, status TEXT NOT NULL, sensitivity_class TEXT NOT NULL,
                  proposed_at_utc TEXT NOT NULL, verified_at_utc TEXT NULL,
                  retracted_reason TEXT NULL);
                """;
            command.ExecuteNonQuery();
        }

        new SqliteDatabase(Factory).Initialize();

        Assert.Equal(SqliteDatabase.TargetSchemaVersion, Version(Factory));

        // Decisions and episodes recorded before there was anywhere to put these read as null
        // rather than as a value nobody chose.
        Assert.Equal(
            "constitutional_assessment_ref",
            ColumnOf(Factory, "decision", "constitutional_assessment_ref"));

        Assert.Equal(
            "effective_genome_ref", ColumnOf(Factory, "life_episode", "effective_genome_ref"));

        // And the tables that arrived whole came with the DDL rather than a migration.
        Assert.Equal("id", ColumnOf(Factory, "mind", "id"));
        Assert.Equal("id", ColumnOf(Factory, "work_item", "id"));
        Assert.Equal("id", ColumnOf(Factory, "incident", "id"));
        Assert.Equal("id", ColumnOf(Factory, "constitutional_assessment", "id"));
    }
}
