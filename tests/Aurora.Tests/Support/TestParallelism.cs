using Xunit;

// Test classes run sequentially, on purpose.
//
// SqliteTestDb.Dispose calls SqliteConnection.ClearAllPools(), which is process-wide: it drops
// pooled connections for every SQLite database in the process, not just the one being disposed.
// Under xUnit's default per-collection parallelism, one test finishing would therefore pull
// connections out from under an unrelated test still in flight.
//
// The alternative is to stop clearing pools and let temp files linger locked. Serialising is
// cheaper to reason about and costs nothing measurable: the whole suite runs in well under a
// second.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
