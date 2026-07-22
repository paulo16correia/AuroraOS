# Aurora OS — RFC 05: Objectives, planning and execution of tasks

**State:** Normative · **Depends on:** RFC 02–03

## Objective

Convert desired results into observable work. The planner proposes a sequence; the tool manager and policy authorize each real effect.

## Architecture and flow

```text
Request/goal → clarify outcome and constraints → candidate plan
                                                │
                                                ▼
dependencies ← tasks ← acceptance criteria
                                                │
approval of the plan? ─┤
                                                ▼
                       agendar tarefa pronta → executar/observar
                                                │
                                                ▼
update state + reflection
```

## Data structures

```text
Goal
  id, title, outcome, owner_id, priority: 1..5
  status: DRAFT|ACTIVE|BLOCKED|PAUSED|COMPLETED|CANCELLED|FAILED
  constraints_json, success_criteria[], deadline_at, budget_json
  created_from_ref, approval_policy_id

Task
  id, goal_id, title, description, kind: RESEARCH|DECISION|TOOL|HUMAN|CHECK
  status: DRAFT|READY|RUNNING|WAITING_INPUT|WAITING_APPROVAL|
          SUCCEEDED|FAILED|SKIPPED|CANCELLED
  dependencies[], inputs_json, expected_output_schema
  risk: LOW|MEDIUM|HIGH|CRITICAL, assignee: AURORA|HUMAN
  retry_policy, idempotency_key, acceptance_tests[]

Plan
  id, goal_id, revision, rationale, assumptions[], task_ids[]
  status: PROPOSED|APPROVED|ACTIVE|SUPERSEDED|CLOSED
```

## Interfaces

```text
Planner.create(goal_request, context) -> Plan
Planner.replan(goal_id, trigger) -> PlanRevision
Scheduler.next_runnable() -> Task[]
TaskService.transition(task_id, target_state, evidence) -> Task
```

## Mandatory rules

1. An objective MUST state outcome and success criteria; “deal with it” requires clarification or discovery plan.
2. Every task transition MUST respect a state machine and attach evidence.
3. `RUNNING` can never coexist with non-`SUCCEEDED` dependency unless explicitly ruled.
4. Automatic repetitions are only allowed for idempotent and on-budget tasks.
5. The planner MUST NOT hide assumptions; should present them when they influence cost, risk or direction.

## Limit and error cases

- **Failed dependency:** block descendants and offer replanning; do not “mark complete” by inference.
- **Objective incompatible with policy:** maintain `BLOCKED` with reason, without decomposing into rule contours.
- **Deadline passed:** notify, pause or continue according to policy, never secretly change the deadline.
- **Invalid task output:** fails validation, saves diagnosis and does not unlock dependencies.

## Justification

Separate objectives and tasks avoid confusing lasting intention with a concrete attempt at execution. Acceptance criteria make progress measurable and auditable.

## Future expansions

Probabilistic estimation, calendars, priority negotiation, approved multi-agent teams and resource optimization.