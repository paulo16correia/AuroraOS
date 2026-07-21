using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace Aurora.Tests.Support;

/// <summary>Controllable clock for deterministic timestamps in tests.</summary>
public sealed class TestClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

/// <summary>An isolated, migrated SQLite database in a temp file, cleaned up on dispose.</summary>
public sealed class SqliteTestDb : IDisposable
{
    private readonly string _path;

    public SqliteTestDb()
    {
        _path = Path.Combine(Path.GetTempPath(), $"aurora-utest-{Guid.NewGuid():N}.db");
        Factory = new SqliteConnectionFactory(_path);
        new SqliteDatabase(Factory).Initialize();
    }

    public SqliteConnectionFactory Factory { get; }

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
                // best effort
            }
            catch (UnauthorizedAccessException)
            {
                // best effort
            }
        }
    }
}
