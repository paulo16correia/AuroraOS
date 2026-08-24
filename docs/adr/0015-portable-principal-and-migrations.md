# Design 0015 — Portable principal, and versioned migrations

**Status:** Implemented · **Date:** 2026-08-23
**Baseline:** `docs/adr/0012-specification-baseline.md`
**Relates to:** step 2 of `docs/100-implementation-order.md` (*State Manager … versioned migrations*)

## The specification was already portable

Checked before changing anything: the RFCs, Laws and governance documents contain no
Windows-specific model. The four occurrences of the word are unrelated — "two windows approve",
"availability windows", "time windows", and a note about a CLI in one execution trace.

**Nothing in `docs/` needed correcting.** The platform coupling was entirely ours, introduced by the
first phase and carried in this repository's own records and code.

## What changed

`Principal.WindowsUser` becomes `Principal.OsUser`, and the database columns
`principal_windows_user` become `principal_os_user` in `audit_record`, `approval` and
`consent_session`. The value was always `Environment.UserName`, which is portable; only the name
claimed otherwise.

A name is not cosmetic here. It reaches the audit schema, which is the record someone reads years
later, and a field called *Windows user* on a Linux host is a false statement in the one place that
must not contain any.

This repository's own ADRs 0001, 0002 and 0010 are aligned to match. The Windows Credential UI
mention in 0001 is now "the platform's own credential UI where one exists", since it described one
platform's option for the still-deferred desktop dialog.

## Versioned migrations

Step 2 of the implementation order requires versioned migrations. A `schema_version` table existed
and nothing read it; renaming a column made that gap concrete.

`SqliteDatabase` now carries a `TargetSchemaVersion` and a migration per version:

- A **fresh** database is created by the DDL at the current shape and stamped at the target without
  running any migration.
- An **existing** database is brought forward one version at a time.
- Migrations run inside the same transaction as the DDL, so a half-applied migration cannot survive
  a crash.

Migration v2 renames the three columns. A rename preserves the values, so the audit chain still
verifies afterwards — the hash pre-image covers what the fields contain, not what they are called.
A test builds a database in the previous shape, migrates it, and asserts both the new column and the
surviving row value.

## Consequences

Unlike the audit pre-image changes recorded in `docs/adr/0006-it3b-audit-preimage.md`, this one is
migratable: an existing database is upgraded in place rather than refused at startup. That is the
behaviour every future schema change should aim for, and the runner added here is what makes it
possible.
