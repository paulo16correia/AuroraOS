# ADR-015 — Explicit dependencies between tasks in a plan

**Status:** accepted
**Date:** 2026-07-18

## Decision

Each `Task` starts to declare its position (`sequence`) and the tasks that must be completed first (`dependency_refs`). The task coordinator is the only component that interprets these dependencies; the Planner proposes steps and the executors remain isolated.

## Justification

A `Plan` with steps without relationships is just a list. Persistent dependencies allow you to resume work, audit why a task is blocked, and later introduce parallelism without changing the fundamental model.

## Consequences

- The first operational plan now has analysis and recommendation as separate tasks.
- Compatibility with already persisted tasks is maintained by default values.
- VS-016 can add scheduling or wait states without restructuring the `Task` contract.