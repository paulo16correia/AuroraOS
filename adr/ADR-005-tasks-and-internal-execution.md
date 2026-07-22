# ADR-005 — Tasks and controlled internal execution cycle

**Status:** ACCEPT
**Date:** 2026-07-17
**RFCs affected:** RFC 05, RFC 021, RFC 022, RFC 040, LAW-003, LAW-006

## Context

VS-005 allows the Entity to propose a Plan, but does not demonstrate that the plan produces verifiable progress. Directly linking a Plan to an external Action would eliminate the authorization boundary defined by the Constitution and LAW-006.

## Decision

VS-006 introduces persistent `Task` between `Plan` and `Observation`. The only initial Task is `INTERNAL_ANALYSIS`: it is created by the Kernel from an explicit Plan, runs locally and produces a controlled textual result. It has no network client, file system, tool, scheduler or capacity provider.

```text
Goal → Plan(PROPOSED) → Task(READY → RUNNING → COMPLETED) → Observation → Reflection → GoalEvaluation
```

Upon completing the Task, the Kernel creates `GoalEvaluation(PROGRESS_DETECTED, progress_delta=1)` with references to Task, Observation and Reflection. The Task is included in the shutdown `MindState`, even when completed, to preserve continuity and auditability.

## Consequences

- Execution exists only in the internal domain and is deterministic.
- `TaskCreated`, `TaskStarted` and `TaskCompleted` make the cycle auditable.
- External requests, including sending emails, may generate internal analysis; never generate `Action`, `ToolCall` or `CapabilityRequest` in this slice.
- An already persisted Task is loaded, not recreated or reexecuted during restoration.

## Migration and rollback

There is no retroactive creation of Tasks. Disabling VS-006 prevents new internal executions, but preserves existing Tasks, snapshots, and events for replay and auditing.