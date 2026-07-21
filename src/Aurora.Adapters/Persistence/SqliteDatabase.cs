using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Persistence;

/// <summary>
/// Idempotent schema bootstrap for the Aurora persistence store. Safe to invoke repeatedly;
/// every statement uses <c>IF NOT EXISTS</c> semantics and the seed row is only written when absent.
/// </summary>
public sealed class SqliteDatabase
{
    private const string SchemaDdl = """
        CREATE TABLE IF NOT EXISTS schema_version (
          version INTEGER NOT NULL
        );

        INSERT INTO schema_version (version)
        SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_version);

        CREATE TABLE IF NOT EXISTS audit_record (
          sequence INTEGER PRIMARY KEY AUTOINCREMENT,
          record_id TEXT NOT NULL,
          principal_client_id TEXT NOT NULL,
          principal_windows_user TEXT NOT NULL,
          action_id TEXT NOT NULL,
          input_hash TEXT NOT NULL,
          outcome TEXT NOT NULL,
          created_at_utc TEXT NOT NULL,
          previous_hash TEXT NOT NULL,
          record_hash TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS idempotency (
          principal_client_id TEXT NOT NULL,
          idempotency_key TEXT NOT NULL,
          request_hash TEXT NOT NULL,
          state TEXT NOT NULL,
          result_json TEXT NULL,
          created_at_utc TEXT NOT NULL,
          updated_at_utc TEXT NOT NULL,
          PRIMARY KEY (principal_client_id, idempotency_key)
        );
        """;

    private readonly SqliteConnectionFactory _factory;

    public SqliteDatabase(SqliteConnectionFactory factory) => _factory = factory;

    /// <summary>Creates the schema if it does not yet exist. Idempotent.</summary>
    public void Initialize()
    {
        using var connection = _factory.Open();

        // journal_mode cannot be changed inside a transaction; set it first. WAL persists on the file.
        using (var walCommand = connection.CreateCommand())
        {
            walCommand.CommandText = "PRAGMA journal_mode=WAL;";
            walCommand.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction(deferred: false);
        using (var ddlCommand = connection.CreateCommand())
        {
            ddlCommand.Transaction = transaction;
            ddlCommand.CommandText = SchemaDdl;
            ddlCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
