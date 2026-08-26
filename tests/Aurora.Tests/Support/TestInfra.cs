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
        _path = TestTemp.Path("db") + ".db";
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

/// <summary>
/// Temporary paths for tests, all under one directory that goes away with the run.
/// </summary>
/// <remarks>
/// Written after a test run filled the machine's disk. Tests were scattering key files, anchor
/// files and sandbox roots directly into the system temporary directory and deleting almost none
/// of them — 5,800 leftovers from one afternoon. Individually tiny, and collectively the reason a
/// suite that had passed a hundred times started failing at random with SQLite unable to write.
/// <para>
/// One directory per process, removed on exit. A test that wants a path asks for one and does not
/// have to remember to clean it up, because remembering is exactly what did not happen.
/// </para>
/// </remarks>
public static class TestTemp
{
    private static readonly string Root = Create();

    private static string Create()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"aurora-tests-{Environment.ProcessId}-{Guid.NewGuid():N}");

        System.IO.Directory.CreateDirectory(root);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                System.IO.Directory.Delete(root, recursive: true);
            }
            catch (Exception leftBehind) when (leftBehind is IOException or UnauthorizedAccessException)
            {
                // One directory left on a crash is a thousand fewer than before.
            }
        };

        return root;
    }

    /// <summary>A path inside the run's directory. The file need not exist.</summary>
    public static string Path(string prefix) =>
        System.IO.Path.Combine(Root, $"{prefix}-{Guid.NewGuid():N}");

    /// <summary>A directory inside the run's directory, created.</summary>
    public static string Folder(string prefix)
    {
        var path = Path(prefix);
        System.IO.Directory.CreateDirectory(path);
        return path;
    }
}
