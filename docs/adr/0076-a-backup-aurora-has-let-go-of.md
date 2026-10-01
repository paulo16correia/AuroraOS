# Design 0076 — A backup Aurora has let go of

**Status:** Implemented · **Date:** 2026-09-07
**Rests on:** `docs/adr/0009` (backup and restore)

## Why now

On Windows, a backup that had just been reported complete could not be read:

```
IOException: The process cannot access the file because it is being used by another process.
  aurora-19700101T000000Z.db
```

## Pooling makes disposal a lie

`SqliteConnectionFactory` opened every connection with `Pooling = true`, which is right for the live
database: connections are taken and returned thousands of times, and the file stays open for as long
as Aurora runs anyway.

A backup is not that. `BackupAsync` opens the destination, writes the snapshot through SQLite's own
backup API, disposes it, and is finished with the file forever. Pooled, that `Dispose` returns the
connection to the pool and the operating system handle stays open — and `VerifyAsync` then opens the
file a second time, through a second factory, and leaves a second one.

The handle SQLite holds is a **writing** one. `File.ReadAllBytes` asks for `FileShare.Read`, which
refuses to coexist with a writer, so the next reader is refused: a backup being checked, an archive
being made, or the owner copying the file somewhere safe.

Unix has no mandatory locking and hides all of it. That is why this survived every run of the suite
until the first one on Windows — and why the fix comes with a test that opens the file with
`FileShare.None` and then deletes it, both of which fail on Windows while any handle is open and
neither of which is a meaningful assertion on its own.

## Ownership, said out loud

`SqliteConnectionFactory` takes `pooled`, defaulting to `true` so the live database is untouched.
`SqliteBackupService` builds its factories with `pooled: false`, through one private `Snapshot`
method used by both the writing and the verifying path, so there is one place that decides it.

What was rejected, and why:

- **Retrying the read, or sleeping first.** The handle does not close on its own. This would have
  turned a certainty into a flake.
- **`SqliteConnection.ClearAllPools()`.** It works, and it reaches every database in the process to
  fix one file. A backup finishing is not a reason to drop the live database's pool.
- **Copying the file instead of using SQLite's backup API.** `docs/adr/0009` settled that: a plain
  copy of a WAL database while writers are active can capture a torn state that only fails at
  restore, when the original may be gone.

The property is now stated where it belongs — a backup is a file Aurora writes, checks, and lets go
of — rather than being true by accident on one family of operating systems.
