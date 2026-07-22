# ADR-010 — Boundary of specific executors per operation

**Status:** Accepted
**Date:** 2026-07-18
**Decision:** VS-010 — Real Boundary Executor

## Context

VS-008 demonstrated the transition from `CapabilityRequest` to `CapabilityResult` via a `NOOP_EXECUTOR`. This proof was secure, but it did not express that each external capability will require its own logic, specific validation and an auditable boundary before being executed.

## Decision

Replace `NOOP_EXECUTOR` with `ExecutorRegistry` and `CapabilityExecutor.prepare`. The first registered executor is `EmailSendExecutor`; in this version it only prepares `EMAIL_SEND` and returns `ExecutionPreparation`. You do not have or can access SMTP, HTTP transport, credentials, or system tools.

`ExecutorDispatcher` persists the preparation and produces a `CapabilityResult` that declares `BLOCKED` or `AWAITING_APPROVAL`. External execution is not part of the VS-010 contract.

## Consequences

- Each capability may have an isolated and testable implementation.
- The Kernel continues to own persistence, events, correlation and idempotence.
- Blocking proof becomes more accurate than `NOT_IMPLEMENTED`.
- VS-011 has a concrete preparation to which you can attach approval.

## Rejected alternatives

1. **Activate SMTP already in VS-010** — would introduce an external effect before the approval limit.
2. **Maintain a generic executor with conditionals** — would scatter operation details and make isolation difficult.
3. **Allow LLM to choose the executor** — would violate Kernel and registry control.

## Migration

`NOOP_EXECUTOR` and `NOT_IMPLEMENTED` are no longer the normal result of EMAIL_SEND. Unavailable orders now generate `ExecutionPreparation(BLOCKED)` and `CapabilityResult(BLOCKED)`. Migration does not activate capabilities or require new data.