# ADR-022 — Conservative workflow compensation

**Status:** Accepted
**Date:** 2026-07-19

## Context

A plan can prepare several external capabilities. A failure in one of them can
render the purpose of another invalid — for example, sending a confirmation of
a meeting that has not been created.

## Decision

Introduce `WorkflowCompensation` as a persisted decision and property of the
Kernel. When a capability fails or is in an unknown state, the Kernel:

1. finds sibling requests belonging to the same `Plan`;
2. only cancel approvals still pending;
3. cancels the corresponding waiting Tasks;
4. does not perform external rollback; and
5. Request new information when there are no longer any safe branches to cancel.

## Consequences

- Avoid inconsistent external effects without hiding the flaw.
- Maintains idempotence after restart or replay.
- Does not resolve sagas with real rollback; This requires explicit tradeoffs for
  capability and is outside the VS-023.

## Rejected alternatives

- **Run email despite failure:** may report false status.
- **Automatically reverse all effects:** unsafe; not all APIs
  they allow reliable inversion and some actions are not reversible.
- **Leave both approvals pending:** forces the user to discover the
  inconsistency and allows accidental execution.