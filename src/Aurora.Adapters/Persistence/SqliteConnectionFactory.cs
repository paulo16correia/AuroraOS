using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Persistence;

/// <summary>
/// Creates and opens <see cref="SqliteConnection"/> instances against a single database,
/// applying the connection-level pragmas Aurora relies on (busy timeout and foreign keys).
/// Connections are pooled; WAL (set at initialization) gives every connection a consistent view of
/// committed state without shared-cache SQLITE_LOCKED semantics. Callers own and dispose the result.
/// </summary>
public sealed class SqliteConnectionFactory
{
    private const string PragmaCommand = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";

    private readonly string _connectionString;

    public SqliteConnectionFactory(string dbPath)
    {
        DbPath = dbPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = true,
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
