# Design 0005 — Audit hardening (It.3, first increment)

**Status:** Implemented · **Date:** 2026-08-23
**Depends on:** `docs/adr/0001-mcp-pipeline-slice1.md` (the It.0 SHA-256 chain)

## The hole this closes

It.0 chained audit records with unkeyed SHA-256. That detects an **in-place
edit**: touching a field leaves `record_hash` stale.

It does not detect the other two shapes of tampering, and the It.0 code said so
in a comment:

1. **Truncation.** Deleting the newest records leaves a shorter, perfectly valid
   chain. Nothing in the chain knows how many records ought to exist.
2. **Wholesale rewrite.** Anyone with write access to the file can recompute the
   entire chain from scratch — the hash is public and depends on no secret.

Both are within reach of exactly the adversary the audit log is meant to stop:
someone who already managed to write to the `.db`.

## Two defences

**1. Keyed chain (HMAC-SHA-256)**

`record_hash` becomes an HMAC under a 32-byte key held **outside** the database
(`AuditKeyFile`, created on first use with owner-only permissions). Recomputing
the chain is no longer possible without the secret: write access to the `.db` no
longer suffices to forge.

*Limitation, without hedging:* a file on the same disk only raises the bar.
Anyone who can read arbitrary files as this user obtains the key. Real separation
means an OS keystore (DPAPI/Keychain) or an HSM — deferred, but the class takes
raw key bytes, so changing the source touches only it.

**2. External head anchor**

After each append (and only after the commit, so a rollback does not look like
truncation), the pair `(sequence, record_hash)` is written to a separate file.
Verification compares them: if the anchor is ahead of the database, records were
removed. The anchor **never moves backwards**, so a stale writer cannot rewind it
to hide a deletion.

`AuditVerification` gained a `Reason`, because "chain broken at sequence 7" and
"records missing from 7" are different diagnoses that call for different
responses.

## Anchor scope

The anchor is derived from the database file (`<db>.anchor`), not from its
directory. Two databases in the same folder must not share one anchor: each would
read the other's head as evidence of truncation, and the server would refuse to
start.

This matters beyond naming. A tamper detector with false positives is as useless
as none at all, because the human response to a frequent, wrong alarm is to
switch it off.

## Configuration

`Aurora:AuditKeyPath` (default: `aurora.audit.key` beside the database) and
`Aurora:AuditAnchorPath` (default: `<db>.anchor`). Both configurable on purpose,
so an operator can put the key somewhere the database backups do not reach —
keeping the two together in one backup defeats the defence.

## Tests

7 new tests: truncation detected even though the remaining chain is valid; a
wholesale rewrite failing without the key; an anchor diverging at the same
sequence; an empty log with no anchor verifying cleanly; the anchor refusing to
move backwards; the key created once and reused; and a wrong-length key raising an
error **instead of** regenerating — regenerating would destroy the evidence.

## Deferred

The key in an OS keystore or HSM; an anchor replicated off the machine (today's
defence falls if the attacker deletes database and anchor together); periodic
checkpoint signing; and pre-image enrichment (decision, reason, risk, `via`,
`policy_ids`), which is the next It.3 increment.
