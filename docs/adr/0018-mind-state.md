# Design 0018 — Mind State: capture, verify, restore (step 5c)

**Status:** Implemented · **Date:** 2026-08-23
**Implements:** RFC 043 · **Baseline:** `docs/adr/0012-specification-baseline.md`
**Completes:** step 5 of `docs/100-implementation-order.md`

## A snapshot is not a dump

RFC 043 opens by saying so, and the implementation follows: a snapshot holds **references** —
identity, self, beliefs, goals, plans, attention, world model version, tool and scheduler state —
plus the audit position it belongs to. Enough to resume an operational entity, not merely to
remember things.

The body is encrypted with AES-256-GCM under a key **separate from the vault's**. A compromised
vault key must not also open every Mind State ever captured, and separating them costs one file.

## Rule 1: never pretend atomicity

`ConsistencyLevel.Strict` refuses to capture when any component reports itself inconsistent.
`BestEffort` captures and **names** them on the snapshot.

The RFC's structure list does not include a field for this, but rule 1 requires non-consistent
components to be declared, so `non_consistent_components` was added. A reader must be able to tell
which kind of snapshot they are holding; a best-effort capture that looks identical to a strict one
is the failure the rule exists to prevent.

## Rule 2: restore order

In this order, because it is the order that makes the guarantees hold:

1. Move the instance to `RECOVERING` (RFC 039).
2. Revoke temporary leases — consent sessions are exactly that, so the kill switch built for step
   It.2c is reused rather than a second mechanism invented.
3. List indeterminate tool calls and refuse to act until they are reconciled.

The plan's status is `WAITING_RECONCILIATION` while any `UNKNOWN` idempotency key remains, and the
snapshot is **not** marked `RESTORED` until nothing is left indeterminate. The RFC's own example is
an action that may have been sending an email when the process died: the honest state is "not yet
known", and a restore that declares itself finished while that is true would be lying about it.

The reconciliation policy is recorded as `consult-provider-before-retry` rather than being implied.

## Corrupt snapshots

A body that fails authentication is marked `CORRUPT` and restore refuses it outright, pointing at
`LastVerifiedAsync`. There is no partial restore: half a Mind is not a smaller Mind, it is a
different one with no record of which half is missing.

## A newer body layout is a migration, not a guess

A snapshot whose schema version exceeds what the build reads does not verify, and the report says a
versioned migration is required. Deserialising unknown fields permissively is exactly what the RFC
forbids, and it is how a restore silently drops state it did not recognise.

## Rule 4: export

Sections are built from the components with two filters. Any reference under a `vault://` or
`local://` scheme is removed and the section is listed as redacted — Vault data is never exportable,
and filtering by scheme means a secret reference cannot ride along inside an otherwise innocent
section. Working memory leaves only when the access context allows it, since it is short-retention
by policy.

## Tests

14 conformance tests: strict capture refusing an inconsistent component; best-effort naming it; the
audit position pinned; the body encrypted at rest; a good snapshot verifying; a tampered body marked
corrupt; a corrupt snapshot never partially restored; the last verified snapshot as fallback;
restore moving to `RECOVERING` and revoking leases; restore waiting while a tool call is
indeterminate and leaving the snapshot un-restored; a clean restore completing; export carrying no
vault references; working memory withheld without permission; and a newer schema not read
permissively.

## Deferred

RFC 043's future expansions: incremental snapshots, cross-region recovery, branching for simulation,
cross-vendor migration and cryptographic state attestation. Also retention enforcement — snapshots
carry an `EXPIRED` status that nothing yet sets — and rule 5's treatment of interaction state, which
needs the cognitive cycle at step 7 before it means anything.

## Step 5 is complete

Mind Manager, Genome and Mind State with backup and restore. The gate for steps 4–5 reads
*"permissions deny by default; isolated restoration passes; secrets do not appear in logs"* — all
three now hold, with isolated restore covered here and by the verified backup from `docs/adr/0009`.

## Next

Step 6: Domain Model plus a minimum Memory, Knowledge and World Model.
