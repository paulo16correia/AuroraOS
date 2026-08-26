# Design 0064 — The security events nobody raised

**Status:** Implemented · **Date:** 2026-08-26
**Found by:** the Core conformance pass

## Declared, typed, tested, and produced by nothing

`SecurityEventType` declared six kinds of incident. Four were raised somewhere in the code.
`AuthenticationAbuse` and `PrivilegeEscalation` were raised by nothing at all.

Both had a constant, both had a place in the incident service, both were covered by tests of the
incident service — and no line in Aurora ever produced one. RFC 09 says Aurora watches for these.
It did not.

This is the failure mode the whole conformance pass exists to find: every part is correct, every
test of every part passes, and the thing the parts were supposed to add up to does not happen. A
declared incident type that nothing raises is worse than an absent one, because the operator panel
lists it and an owner reading that list concludes they are being watched over.

## Where each one now comes from

**`AuthenticationAbuse`** — the bearer middleware, when a request arrives with a token that is
well-formed and wrong. Repeated failures against a token nobody mistypes is somebody trying tokens.
The refusal itself does not change; the incident is raised beside it.

**`PrivilegeEscalation`** — two places, because there are two ways to reach for authority you were
not given:

- the guard that refuses the agent a decision reserved for a person, when the agent's own token is
  used to approve something the agent proposed; and
- the plugin bridge, when a plugin invokes a capability outside its manifest.

The second is the one that matters most in practice. A plugin reaching past its declared
permissions is not a bug in the plugin; it is the thing the manifest exists to prevent, and it
should leave a mark that outlives the process.

All three are fire-and-forget beside the refusal. The refusal is the security control; the incident
is the record. Making the refusal wait on the record would let a slow write become a way to hold a
request open.

## An approval that could never be used and could never be replaced

Found while writing the tests for the above.

Approvals had a `PENDING` state, a unique index allowing one pending approval per scope, and an
expiry time — and nothing that ever expired one. So a pending approval that timed out held its
scope for ever: it could not be used, because it had expired, and no new one could be created,
because the index said one was already pending.

The scope was dead until somebody edited the database.

`ApprovalStatus.Expired` now exists and the store retires a timed-out pending approval when it next
looks, which frees the index and lets a fresh request through. An expired approval is not silently
reused and is not silently deleted: it is retired, visibly, in the same table.

## What keeps this from happening again

`DeadContractTests` walks every declared security event type, refusal reason and state constant and
fails on any that nothing in the source produces. See `docs/adr/0065`.
