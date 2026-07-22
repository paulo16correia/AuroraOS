# VS-018 — Persistent Scheduler

## Objective

Resume a `Task` in `WAITING_FOR_TIME` when the persisted time arrives, without
reexecute an already performed transition or call capabilities directly.

```text
Task(WAITING_FOR_TIME, wait_until)
             ↓
PersistentScheduler.tick(now)
             ↓
Task(READY)
             ↓
TASK_RESUMED + trace SCHEDULER
```

## Mandatory rules

- `wait_until` is ISO-8601 with offset.
- Only Tasks won in `WAITING_FOR_TIME` are carried over to `READY`.
- The second tick observes `READY` and does not produce a new transition: it is idempotent.
- The Scheduler does not execute capabilities or grant implicit approval.
- The Task is persisted before the event and trace.

## Acceptance criteria

An expired Task transitions once to `READY`, issues `TaskResumed` with reason
`DUE_TIME`, survives restart and is not resumed on the next tick.