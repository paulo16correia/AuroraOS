# ADR-011 — Explicit and persistent user approval

**Status:** Accepted
**Date:** 2026-07-18
**Decision:** VS-011 — Persistent User Approval

## Context

VS-010 can prepare operations by capability, but preparation is not authorization. Without a persistent approval object, a restart could lose the wait state, repeat the request, or allow conversational text to be interpreted out of context.

## Decision

Add `ApprovalRequest` and `Approval`. The Kernel creates the request only after a pending preparation, and resolves the user's confirmation only when there is a single PENDING request for the Entity. Approval is persisted with user, correlation and instant.

EMAIL_SEND is now only available for **preparation and approval**. There is no external transport; `Approval(APPROVED)` does not trigger execution.

## Consequences

- Authorization survives shutdown/restore and is auditable.
- VS-012 can require a valid Approval without redesigning the cognitive cycle.
- External capabilities remain unable to produce effects in this version.

## Rejected alternatives

1. **Save "yes" as chat message** — contains no secure connection to operation or lifecycle control.
2. **Execute soon after approval** — mixes authorization with external effect before the actual executor is implemented.
3. **Letting LLM decide what was approved** — violates state ownership and allows ambiguity.