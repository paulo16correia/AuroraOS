# Design 0009 — Backup and restore (It.3, fifth increment)

**Status:** Implemented (backup) · Documented (restore) · **Date:** 2026-08-23
**Depends on:** `docs/adr/0005-it3a-audit-hardening.md`

## Backup

`SqliteBackupService.BackupAsync` uses **SQLite's own backup API**, not a file
copy. Copying a WAL file with active writers can capture a torn state that only
fails much later — during a restore, which is the worst possible moment to find
out.

It produces two timestamped files: `aurora-<stamp>.db` and its matching
`.anchor`. The anchor travels with the snapshot because without it a restored
database can only be shown *internally consistent*, never *complete*.

**Verification runs against the copy, not the original.** A backup whose chain
does not verify is worthless, and the moment to learn that is now, not during a
restore when the original may no longer exist.

## The key is NOT in the backup

A deliberate decision, and the most important thing in this document.

`docs/adr/0005-it3a-audit-hardening.md` built the audit defence on one premise: the signing key lives
outside the database, so anyone who gains write access to the database cannot
rewrite the chain and re-sign it.

Putting the key in the same backup throws that away. Whoever steals the archive
has everything they need. A backup is precisely the artefact that leaves the
machine, gets copied to third-party storage, and survives for years — the worst
possible place to reunite the secret with the data it protects.

The key is the operator's responsibility: stored separately, somewhere the
database backups do not reach. A test asserts that no key file appears in the
backup directory.

Accepted consequence: restoring without the key yields a usable database whose
chain **cannot be verified**. That is the correct behaviour — knowing the archive
is unverifiable beats trusting it by default.

## Restore — a procedure, not code

Restore is deliberately not automated. Swapping database files underneath a
running server is how installations get corrupted, and a command that does it
invites doing exactly that in a hurry during an incident.

1. **Stop the Aurora server.** Do not proceed with the process alive.
2. Put `aurora-<stamp>.db` at the `Aurora:DbPath` location and
   `aurora-<stamp>.db.anchor` at `Aurora:AuditAnchorPath`.
3. Delete any leftover `-wal` and `-shm` files from the old database. An orphaned
   WAL from a different database is a reliable route to corruption.
4. Restore the signing key, from wherever it was kept separately, to
   `Aurora:AuditKeyPath`.
5. Start. The server verifies the chain at startup and **refuses to start** on
   failure — if that happens, the response is to investigate, never to delete the
   anchor to silence it.

## Tests

5 new tests: a verifiable snapshot; the anchor included; **the key absent from the
backup**; a backup tampered with after the fact failing verification; and a backup
of an empty database verifying cleanly.

## Deferred

Automatic backup scheduling; rotation and retention; incremental backup;
encryption of the archive at rest; and verifying that a restored `.db` matches its
anchor *before* starting the server, rather than at startup.
