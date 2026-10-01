using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Persistence;

/// <summary>
/// Creates and opens <see cref="SqliteConnection"/> instances against a single database,
/// applying the connection-level pragmas Aurora relies on (busy timeout and foreign keys).
/// Connections are pooled by default; WAL (set at initialization) gives every connection a
/// consistent view of committed state without shared-cache SQLITE_LOCKED semantics. Callers own and
/// dispose the result.
/// </summary>
public sealed class SqliteConnectionFactory
{
    private const string PragmaCommand = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";

    private readonly string _connectionString;

    /// <param name="dbPath">The database file, or an in-memory identifier.</param>
    /// <param name="pooled">
    /// Whether disposing a connection returns it to the pool or closes the file.
    /// </param>
    /// <remarks>
    /// Pooling is right for the live database, where connections are taken and returned thousands
    /// of times and the file stays open for as long as Aurora runs anyway.
    /// <para>
    /// It is wrong for a database Aurora is finished with. A pooled connection keeps the operating
    /// system handle open after <c>Dispose</c>, and on Windows that is not invisible: the handle
    /// SQLite holds is a writing one, so the next process to open the file for reading — a backup
    /// being checked, an archive being made, the owner copying it — is refused with a sharing
    /// violation. Unix has no mandatory locking and hides this entirely, which is why it went
    /// unnoticed until Aurora ran on Windows (docs/adr/0076).
    /// </para>
    /// </remarks>
    public SqliteConnectionFactory(string dbPath, bool pooled = true)
    {
        DbPath = dbPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = pooled,
        }.ToString();
    }

    /// <summary>The data source (file path or in-memory identifier) backing this factory.</summary>
    public string DbPath { get; }

    /// <summary>Opens a connection synchronously and applies the standard pragmas.</summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = PragmaCommand;
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Opens a connection asynchronously and applies the standard pragmas.</summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = PragmaCommand;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
