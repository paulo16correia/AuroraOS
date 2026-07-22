# ADR-016 — Persistent waits in the Workflow Engine

**Status:** accepted
**Date:** 2026-07-18

## Decision

Represent waits as persistent state of `Task`, not as processes blocked in memory. An approval reopens the cycle and transitions the corresponding task to `COMPLETED`.

## Consequences

- The Kernel can restart without losing where the workflow left off.
- The Executor has no responsibility for waits or dependencies.
- VS-018 will be able to wake up `WAIT_FOR_TIME` without changing the task contract.