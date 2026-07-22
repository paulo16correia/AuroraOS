# ADR-004 — Persistent, deliberative, simulation-only planning

**Status:** ACCEPT
**Date:** 2026-07-17
**RFCs affected:** RFC 05, RFC 021, RFC 022, RFC 031, RFC 040, LAW-006

## Context

VS-004 introduced operational continuity: Entity has Goal, EntityState and Need, but still cannot represent a future working hypothesis. Creating a `Action` at this point would confuse an internal need with authorization to act.

## Decision

VS-005 introduces persistent `Plan` as a deliberative proposal. An explicit request from the user — initially, “How could you help me better?” — can materialize the `Goal → Need → Plan(PROPOSED)` thread. The foreground is `ASSISTANCE_PLAN`, referencing the Goal `ASSIST_USER` and the Need `UNDERSTAND_USER`.

The plan contains only steps `ASK_CLARIFICATION` and `PROVIDE_RECOMMENDATION`, both `PENDING` and `SIMULATION_ONLY`. There are no `Action`, `Task`, `ToolCall`, external execution, scheduling, or automatic state change for `REVIEWED`, `APPROVED`, or `COMPLETED`.

## Consequences

- Need continues to be evidence and justification; it does not create effects on its own.
- The Kernel is the only creator and reader of Plans in the slice. The provider receives an already validated Plan, does not invent it.
- A Plan is preserved in the shutdown snapshot and can be loaded in a later session.
- The trace now includes `PLANNING`, `PLAN_CREATED` or `PLAN(LOADED)`; the corresponding events are `PlanCreated` and `PlanLoaded`.
- Moving to approval and execution requires subsequent slice and ADR, including policy, Capability Boundary and mandatory Observation.

## Migration and rollback

There is no data migration: Existing Entities do not receive Plans automatically. Removing VS-005 disables Plans proposal and exposure, but preserves objects and events for auditing.