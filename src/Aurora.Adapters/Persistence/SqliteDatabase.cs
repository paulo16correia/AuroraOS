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
          record_hash TEXT NOT NULL,
          risk TEXT NULL,
          via TEXT NULL,
          decision TEXT NULL,
          policy_ids TEXT NULL,
          reason TEXT NULL
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

        CREATE TABLE IF NOT EXISTS approval (
          approval_id TEXT PRIMARY KEY,
          principal_client_id TEXT NOT NULL,
          principal_windows_user TEXT NOT NULL,
          action_id TEXT NOT NULL,
          scope_hash TEXT NOT NULL,
          status TEXT NOT NULL,
          created_at_utc TEXT NOT NULL,
          expires_at_utc TEXT NOT NULL,
          decided_at_utc TEXT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS idx_approval_one_live_pending
          ON approval(principal_client_id, action_id, scope_hash)
          WHERE status = 'PENDING';

        CREATE INDEX IF NOT EXISTS idx_approval_scope
          ON approval(principal_client_id, action_id, scope_hash);

        CREATE TABLE IF NOT EXISTS vault_item (
          id TEXT PRIMARY KEY,
          provider TEXT NOT NULL,
          locator TEXT NOT NULL,
          purpose TEXT NOT NULL,
          allowed_tool_ids TEXT NOT NULL,
          rotation_due_at_utc TEXT NULL,
          status TEXT NOT NULL,
          nonce BLOB NOT NULL,
          ciphertext BLOB NOT NULL,
          tag BLOB NOT NULL,
          created_at_utc TEXT NOT NULL,
          updated_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_vault_status ON vault_item(status);

        CREATE TABLE IF NOT EXISTS domain_event (
          sequence INTEGER PRIMARY KEY AUTOINCREMENT,
          event_id TEXT NOT NULL UNIQUE,
          type TEXT NOT NULL,
          schema_version INTEGER NOT NULL,
          producer TEXT NOT NULL,
          occurred_at_utc TEXT NOT NULL,
          correlation_id TEXT NOT NULL,
          causation_id TEXT NULL,
          aggregate_ref TEXT NULL,
          payload_json TEXT NULL,
          payload_ref TEXT NULL,
          sensitivity TEXT NOT NULL,
          idempotency_key TEXT NULL,
          integrity_hash TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_event_type ON domain_event(type, sequence);

        CREATE TABLE IF NOT EXISTS subscription (
          id TEXT PRIMARY KEY,
          consumer TEXT NOT NULL,
          event_types TEXT NOT NULL,
          filter_ref TEXT NULL,
          mode TEXT NOT NULL,
          checkpoint INTEGER NOT NULL,
          status TEXT NOT NULL,
          max_attempts INTEGER NOT NULL,
          max_schema_version INTEGER NOT NULL,
          diagnosis TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS delivery (
          delivery_id TEXT PRIMARY KEY,
          event_id TEXT NOT NULL,
          subscription_id TEXT NOT NULL,
          attempt INTEGER NOT NULL,
          delivered_at_utc TEXT NULL,
          status TEXT NOT NULL,
          last_error TEXT NULL
        );

        -- Makes fan-out re-executable (RFC 050 rule 1): replaying publication after a crash
        -- cannot create a second delivery for the same (event, subscription).
        CREATE UNIQUE INDEX IF NOT EXISTS idx_delivery_unique
          ON delivery(event_id, subscription_id);

        CREATE INDEX IF NOT EXISTS idx_delivery_status ON delivery(status);

        CREATE TABLE IF NOT EXISTS consent_session (
          session_id TEXT PRIMARY KEY,
          principal_client_id TEXT NOT NULL,
          principal_windows_user TEXT NOT NULL,
          server_boot_id TEXT NOT NULL,
          policy_version TEXT NOT NULL,
          status TEXT NOT NULL,
          actions_used INTEGER NOT NULL,
          max_actions INTEGER NOT NULL,
          created_at_utc TEXT NOT NULL,
          expires_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_session_live
          ON consent_session(principal_client_id, server_boot_id, policy_version, status);

        CREATE TABLE IF NOT EXISTS remembered_note (
          note_id TEXT PRIMARY KEY,
          principal_client_id TEXT NOT NULL,
          note TEXT NOT NULL,
          created_at_utc TEXT NOT NULL
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
