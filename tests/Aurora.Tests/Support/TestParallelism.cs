using Xunit;

// Test classes run sequentially, on purpose.
//
// SqliteTestDb.Dispose calls SqliteConnection.ClearAllPools(), which is process-wide: it drops
// pooled connections for every SQLite database in the process, not just the one being disposed.
// With xUnit's default per-collection parallelism, one test finishing could therefore pull
// connections out from under an unrelated test mid-operation, producing sporadic failures in
// whichever test happened to be in flight — the symptom looked like flakiness in the audit and
// backup tests, which had nothing to do with the cause.
//
// The alternative fix is to stop clearing pools and let temp files linger locked. Serialising is
// cheaper to reason about and costs nothing measurable: the whole suite runs in well under a
// second.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
