using Aurora.Adapters.Events;
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

/// <summary>
/// A real Event Bus over a test database.
/// </summary>
/// <remarks>
/// Not a fake. Components that publish state changes are only correct if what they publish passes
/// the declared contracts (LAW-007), and a stub that accepts anything would hide exactly the
/// mistakes that check exists to catch.
/// </remarks>
public static class TestBus
{
    public static SqliteEventBus Over(SqliteConnectionFactory factory, IClock clock) =>
        new(factory, new SqliteOutbox(new PermissiveEventCatalogue(), clock), clock);
}
