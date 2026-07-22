# Aurora OS — Execution Trace Specification: VS-006 Controlled Internal Execution Loop

**Status:** Authorized by ADR-005
**Use case:** explicit request → Plan → internal Task → Observation → Goal progress, without external effects

## Objective

Demonstrate verifiable cognitive progress without granting external power. VS-006 adds the `Task` layer and ends after a controlled internal execution.

## Normative flow

```text
User: "Como podes ajudar-me?"
  → Signal → Context → Goal(ASSIST_USER) + Need(UNDERSTAND_USER)
  → PLANNING → PlanCreated(ASSISTANCE_PLAN, PROPOSED)
  → TASK(CREATED, INTERNAL_ANALYSIS, READY)
  → TASK(RUNNING) → TASK_COMPLETED(result="Need more user context")
  → Thought → Decision(RESPOND) → Response
  → Observation → Reflection
  → GoalEvaluation(PROGRESS_DETECTED, +1, evidence=[Task, Observation, Reflection])
  → Memory

shutdown → MindState(task_refs[]) → restore → TaskLoaded
```

## Minimum contract

```text
Task
  task_id: "task:{entity_id}:assist-user:internal-analysis"
  entity_id
  goal_ref: Goal(ASSIST_USER)
  plan_ref: Plan(ASSISTANCE_PLAN)
  type: INTERNAL_ANALYSIS
  status: READY|RUNNING|COMPLETED|FAILED
  result_summary: string?
  version, created_at, updated_at
```

## Mandatory rules

1. The Task MUST reference a Plan and Goal belonging to the same Entity.
2. VS-006 can only create `INTERNAL_ANALYSIS`; any other type fails explicitly.
3. The transition is strict: `READY → RUNNING → COMPLETED|FAILED`. A completed Task cannot be re-executed.
4. Task cannot create `Action`, `ToolCall`, `CapabilityRequest`, network call, file write or schedule.
5. Each completed Task MUST generate evidence for `GoalEvaluation` after Observation and Reflection.
6. External request without capability, such as “Send an email for me”, is analyzed internally and ends without external effect.
7. Snapshot and restore preserve Task references, including Tasks `COMPLETED`.

## Events, traces and errors

| Situation | Event | Trace | Result |
| --- | --- | --- | --- |
| New plan | `TaskCreated` | `TASK(CREATED)` | Task `READY` persisted. |
| Internal execution | `TaskStarted` | `TASK(RUNNING)` | No exit to the outside world. |
| Conclusion | `TaskCompleted` | `TASK_COMPLETED` | Result and version persisted. |
| Restoration | `TaskLoaded` | `TASK(LOADED)` | Does not recreate or rerun. |
| Invalid type/invalid transition | no effect | error controlled | Task preserved for audit. |

## Acceptance criteria

1. Entity Birth continues to create Goal `ASSIST_USER`.
2. “How can you help me?” creates Plan and Task, and completes the internal Task.
3. Shutdown/restore preserves the Task with the same identifier, state and result.
4. “Send an email for me” can create internal Task, but does not create Action, ToolCall or CapabilityRequest; the trace maintains `CAPABILITY(NOT_REQUIRED)`.
5. Suite VS-000–VS-005 remains green.

## Deliberate limits

There are no external executor, tools, capability registry, scheduler, simulated actions, retry, parallelism or Task approval. VS-006 measures internal progress, not authorization to change the world.