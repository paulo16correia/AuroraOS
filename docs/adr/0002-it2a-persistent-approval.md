# Design 0002 — Persistent Approval (It.2, first increment)

**Status:** Implemented · **Date:** 2026-07-24
**Depends on:** `docs/adr/0001-mcp-pipeline-slice1.md` (It.0)

## Objective

It.0 left `IConsentGate` refusing every capability at ≥MEDIUM ("real sessions
arrive in It.2"). There was no path at all for a MEDIUM action to execute.
This slice implements the first real piece of It.2: **a persisted approval
bound to the exact (action, input) pair**, plus the first capability with a
genuine state effect (`memory.remember` / `memory.recall`) to exercise it end
to end.

This is not the full "Consent Session (DaVault model)" described in design
0001 — that remains open (see "Deferred" below). It is a smaller, safer
increment, testable without a desktop UI or a passphrase, which replaces the
unconditional refusal with a request → decide → consume flow, single-use per
scope.

## Data model

```
Approval
  approval_id, principal_client_id, principal_windows_user
  action_id, scope_hash            -- scope_hash = the Kernel's existing requestHash
                                       (Sha256(action_id + canonical input));
                                       any change to the input changes the scope_hash.
  status: PENDING | APPROVED | REJECTED | CONSUMED
  created_at_utc, expires_at_utc, decided_at_utc
```

A single 15-minute window covers request, decision and consumption: if the
approval is not decided and consumed within it, it expires, and a new request
for the same scope creates a fresh `PENDING`. This is a deliberate
simplification — design 0001 distinguishes a decision TTL from session reuse;
here there is only one TTL, and the approval is single-use (it does not linger
as a session covering future requests).

## Flow

```
aurora_execute(action_id=memory.remember, input={note})
  → Policy: MEDIUM + approval_required → ALLOW (previously: always DENY)
  → Consent: no live approval for this scope_hash → creates PENDING
  → response: status=denied, error.code=approval_required, consent.approval_id=<id>

aurora_approve(approval_id=<id>, decision=approved|rejected)
  → PENDING (same principal) → APPROVED|REJECTED, audited

aurora_execute(action_id=memory.remember, input={note})   -- the exact same input
  → Consent: live APPROVED, not yet consumed → consumes it (one-time), grants
  → Executor writes the note → completed
```

A rejection is a deliberate human decision: the record stays `REJECTED` and the
same `scope_hash` remains denied (`consent_required`, terminal) until the
content changes. There is no silent re-request for the same input.

## Interaction with idempotency (a required fix)

The Kernel already reserved the `idempotency_key` (state `ACCEPTED`) before
evaluating consent. Before this slice, a consent refusal always settled that
reservation as a terminal `FAILED` — harmless while MEDIUM had no approval path
at all. It would now be a real deadlock: the same `idempotency_key` reused after
the approval was granted would replay the old denial forever (`ReplayFailed`).

The fix: when the consent decision is `requires_approval` (retryable), the Kernel
**abandons** the reservation instead of closing it as a failure
(`IIdempotencyStore.AbandonAsync`, a new method — `DELETE ... WHERE state =
'ACCEPTED'`, compare-and-set). A later attempt with the same key starts a fresh
`Begin` reservation. An explicit rejection still settles as terminal `FAILED`,
because it is not retryable for the same input.

## Security invariants

- Fail-closed is unchanged: only capabilities explicitly marked
  `approval_required` gain an approval path; everything else at ≥MEDIUM stays
  refused.
- `scope_hash` binds the approval to the exact action + input pair (reusing the
  hash the Kernel already computed for idempotency) — changing one field
  invalidates the approval, as in RFC 01 of the reference spec under `docs/`.
- `aurora_approve` only decides a `PENDING` belonging to the same
  `principal_client_id`; it never accepts the `approval_id` as proof of identity.
- Every decision (request, approval, rejection) lands in the existing
  hash-chained audit log.
- An approval never covers a different action or a different input — there is no
  session reuse in this increment.

## Adopted now / Deferred

**Adopted:** approval persisted in SQLite; `aurora_approve` as a third MCP tool;
`memory.remember` (MEDIUM, approval_required) and `memory.recall` (LOW, read) as
the first capability with a real effect; the idempotency fix that allows a retry
after approval.

**Deferred (the full Consent Session, see design 0001):** a time-boxed session
reusable across multiple actions; `session_id` bound to server boot and policy
version; a dedicated desktop dialog with a passphrase (KDF, throttling,
revocation); single-flight prompt serialisation; SSE heartbeats and abort on
disconnect; a ceiling on action count and cost per session; a kill switch. All
of it remains for the repository owner to decide before moving to reusable
sessions with write effects.
