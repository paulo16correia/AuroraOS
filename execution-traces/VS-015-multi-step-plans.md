# VS-015 — Controlled Multi-Step Planes

## Objective

Evolve the persistent plan from a single internal task to a chain of ordered tasks, with explicit dependencies and recovery after restart.

## Flow

```text
Goal(ASSIST_USER)
      |
      v
Plan(ASSISTANCE_PLAN)
      |
      +--> Task #1 INTERNAL_ANALYSIS ── COMPLETED
                                      |
                                      v
      +--> Task #2 PROVIDE_RECOMMENDATION ── COMPLETED
                                      |
                                      v
                              Capability / Policy / Approval
```

## Changes to the model

`Task` now carries `sequence` and `dependency_refs`. Both are persisted, immutable in each version of the task and included in the Mind snapshot.

## Mandatory rules

1. A task can only enter `RUNNING` if all dependencies are `COMPLETED`.
2. The creation of a task is idempotent due to its deterministic identifier.
3. Restoring an entity does not recreate or repeat already completed tasks.
4. The email capability continues to be anchored to the original analysis task, preserving the identifier and idempotence of existing requests.
5. This slice does not create additional external actions; maintains the Policy → Approval → Executor boundary.

## States

```text
READY -> RUNNING -> COMPLETED
  |        |
+--------+--> FAILED (reserved for future executor)
```

## Limit cases

- A second message for the same plan only loads existing tasks.
- If the first task has not yet finished, the second is not created.
- A previous database without `sequence` or `dependency_refs` maintains compatibility through contract defaults.

## Acceptance criteria

- The entity persists both tasks and the `Task #2 -> Task #1` dependency.
- Shutdown/restore preserves the chain.
- The email flow and capabilities policy remain idempotent.
- All regression tests remain green.

## Future expansions

- conditional branches, parallel tasks and junctions;
- `WAITING_FOR_APPROVAL` status connected to a capability;
- recovery of interrupted tasks;
- plans created and validated by the Kernel through the governed MCP path.