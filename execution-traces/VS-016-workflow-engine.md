# VS-016 — Persistent Workflow Engine

## Objective

Introduce workflow states that survive restart and represent a plan as a simple DAG: dependent tasks, a parallel branch, and an explicit wait for approval.

## Flow implemented

```text
INTERNAL_ANALYSIS ──> PROVIDE_RECOMMENDATION
         |
         +────────────> PREPARE_AUDIT_CONTEXT
         |
         +────────────> WAIT_FOR_APPROVAL
                                  |
                                  v
                            APPROVAL recebida
                                  |
                                  v
                              COMPLETED
```

## Model `Task`

In addition to `sequence` and `dependency_refs`, a task can now declare `activation_condition`, `wait_kind`, `wait_ref` and `wait_until`. The reserved states are `WAITING_FOR_EVENT`, `WAITING_FOR_APPROVAL`, and `WAITING_FOR_TIME`.

## Rules

1. A waiting task is never executed by the external executor.
2. Approval is an event that only resumes tasks `WAITING_FOR_APPROVAL` from the active entity.
3. The parallel branch has the same dependency as the main branch, but no dependency between them.
4. There is no polling or temporal awakening in this VS; `WAIT_FOR_TIME` will be agreed by the Scheduler in VS-018.
5. All transitions produce events and traces: `TaskWaiting` and `TaskResumed`.

## Limit cases

- Restart during waiting: the task maintains `WAITING_FOR_APPROVAL`.
- Repeated confirmation: the completed task will not be carried over again.
- Policy that blocks execution: the wait is resumed by the approval decision, but the capability remains blocked by the policy.

## Expansions

Declarative conditions, typed external events, timeouts, branch joins, real parallelism and autonomous scheduler.