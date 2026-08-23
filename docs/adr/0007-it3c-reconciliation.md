# Design 0007 — Reconciling indeterminate executions (It.3, third increment)

**Status:** Implemented · **Date:** 2026-08-23
**Depends on:** `docs/adr/0001-mcp-pipeline-slice1.md` (the It.0 idempotency states)

## The problem

It.0 idempotency has an `EXECUTING` state that is, deliberately, **not
retryable**: if a request is mid-effect, repeating it may duplicate that effect.
While the process is alive, this is correct.

If the process dies mid-effect, the row stays `EXECUTING` forever. The
idempotency key is **wedged**: every later attempt receives `in_progress` from an
execution that no longer exists, with no way out. Neither retry nor give up.

Note that this is **not** the cancellation path, which already settled as
`UNKNOWN`. The hole is abrupt death, where none of our code gets to run.

## The solution

`ReconcileStaleAsync` moves `EXECUTING` reservations whose `updated_at_utc` is
older than a configurable window to `UNKNOWN`, and runs at server startup before
traffic is accepted.

`UNKNOWN` is the honest answer: the effect **may** have happened. We cannot tell.
The caller receives `unknown_state` instead of a false `in_progress`, which is the
difference between "wait a little longer" and "check what happened before trying
again".

Three decisions that matter:

- **`EXECUTING` only.** `ACCEPTED` rows never attempted the effect, so they are
  abandonable rather than indeterminate; treating them as indeterminate would be
  needless pessimism about a harmless reservation.
- **The window is measured from `updated_at_utc`**, stamped when the row enters
  `EXECUTING`. It times the start of the *effect*, not of the request.
- **Default 15 minutes**, configurable. A short window would declare a slow but
  live execution indeterminate — stealing its reservation from under it would be
  worse than the wedge we are fixing.

## Tests

4 new tests: a stale `EXECUTING` reservation becomes `UNKNOWN` and the key is no
longer wedged; a recent `EXECUTING` is left alone; an old `ACCEPTED` is ignored;
and reconciliation is idempotent (the second pass moves zero).

The first test asserts the wedge explicitly **before** reconciling, so the test
documents the problem and not only the fix.

## Deferred

Periodic reconciliation at runtime (it only runs at startup); an operator tool for
inspecting `UNKNOWN` rows; and automatic correlation with the audit log to guess
whether the effect actually happened — that requires each capability to be able to
say whether its effect is verifiable, which is another increment's work.
