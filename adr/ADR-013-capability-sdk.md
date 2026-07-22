# ADR-013 — Stable capabilities SDK

**Status:** Accepted
**Date:** 2026-07-18
**Decision:** VS-013 — Capability SDK

## Context

VS-012 proved a real capability, but the Dispatcher still had special knowledge of the mail executor. Repeating this pattern for each integration would grow the Kernel indefinitely.

## Decision

Set `CapabilityExecutor` with `prepare`, `execute` and `recover`, and enter `CapabilityExecutorRegistry`. Migrate `EmailSendExecutor` to this contract. The registry is built in the runtime composition, not by the cognitive cycle.

## Consequences

- Adding a capability becomes registering a compatible executor.
- Recovery becomes an explicit responsibility of each integration.
- The Kernel maintains ownership of state, events, approval and idempotence.

## Rejected alternatives

1. **A generic executor with conditionals per capability** — concentrates complexity and couples the Kernel to integrations.
2. **Plugins with direct access to the repository** — violates ownership and auditability.
3. **Executors without defined recovery** — makes failures and restarts unsafe.