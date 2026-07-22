# ADR-008 — Executor Interface with results without external effect

**Status:** ACCEPT
**Date:** 2026-07-18
**RFCs affected:** RFC 021, RFC 040, RFC 051, ADR-006, ADR-007, LAW-002, LAW-003, LAW-006

## Context

The Kernel already represents the external intent as `CapabilityRequest`, but it lacks an explicit contract for the component that, in the future, could transform an authorized request into an observable effect. Connecting a real executor now would mix interface, politics and external integration.

## Decision

VS-008 introduces `Executor` and `CapabilityResult`. The Kernel dispatches the validated request to an effectless reference executor (`NoopExecutor`). This executor has no credentials, network, files, plugins, providers or tool calls; returns only `NOT_IMPLEMENTED` or `DRY_RUN`.

```text
CapabilityRequest → Executor Interface → CapabilityResult
````

CapabilityResult` is persisted, auditable and does not change the state of the request. No VS-008 results represent submission, execution, external success, or authorization.

## Consequences

- The execution boundary now exists and is testable without opening access to the outside.
- Aurora can distinguish unavailable capacity from not-yet-implemented executor.
- Future integrations must implement the same contract, behind policy and approval.
- The Kernel continues to own context, state, memory, objectives and decisions; executors are limited consumers of requests.

## Migration and rollback

There are no destructive migrations. Existing requests can now receive a reference result in the next eligible cycle. Removing VS-008 prevents the creation of new results while preserving the existing audit.