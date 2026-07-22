# Aurora OS — Execution Trace Specification: VS-004 Goals + Controlled Agency Loop

**Status:** Authorized by ADR-003
**Use case:** create operational purpose → help → evaluate progress → restore → describe objective

## Objective

Demonstrate that an Entity has persistent operational direction without executing silent autonomy. VS-004 creates initial Goal, EntityState and Need; the Kernel evaluates progress after Reflection and before persisting cycle memory.

## Normative flow

```text
CreateEntity(Roberto, purpose="help you evolve in programming")
  → GoalCreated(ASSIST_USER, ACTIVE)
  → EntityStateInitialized(READY, USER_ASSISTANCE, energy=1.0)
  → NeedDetected(UNDERSTAND_USER, intensity=0.4)

User: "Estou a aprender Python."
  → ciclo normal → Observation → Reflection
  → GoalEvaluation(progress_events +1, evidence=interaction)
  → MemoryCreated(LEARNING_TOPIC, Python)
→ NeedEvaluation(SATISFIED when there is enough context)

shutdown → restore

User: "What is your goal?"
  → Goal carregado pelo Kernel
→ "My active goal is to help you advance in programming."
```

## Minimum contracts

```text
Goal
  goal_id: "goal:{entity_id}:assist-user"
  entity_id, type: ASSIST_USER, title, outcome
  status: ACTIVE, priority: 0.8
  progress_events: 0..n, evidence_refs[], version
  created_from_ref: Identity, created_at, updated_at

EntityState
  state_id: "entity-state:{entity_id}"
  entity_id, availability: READY|STOPPED
  focus: USER_ASSISTANCE, energy: 0..1
  pending_goals, updated_at, version

Need
  need_id: "need:{entity_id}:understand-user"
  entity_id, kind: UNDERSTAND_USER, reason
  intensity: 0..1, status: DETECTED|SATISFIED
  recommended_goal_ref, evidence_refs[], updated_at

GoalEvaluation
  evaluation_id, entity_id, goal_id, observation_id
  progress_delta, evidence_refs[], outcome, evaluated_at
```

## Mandatory rules

1. The initial Goal derives only from the purpose persisted in Identity; later parameters do not override it.
2. `progress_events` only increases with Observation/Reflection of the current cycle and recorded evidence.
3. EntityState is operational, not emotional; `energy=1.0` is a baseline of capability, not sentiment.
4. Need does not create an external effect, additional Goal, Plan or Task. It is only registered and can be read by the future Kernel/UI.
5. The answer to “What is your goal?” comes from Goal `ACTIVE` loaded by the Kernel, not from a textual instruction in the provider.
6. Shutdown snapshot includes references to Goal, EntityState and Need.

## Events and trace

| Situation | Event | Trace |
| --- | --- | --- |
| Birth/compatibility | `GoalCreated`, `EntityStateInitialized`, `NeedDetected` | `GOAL(CREATED)`, `ENTITY_STATE(INITIALIZED)`, `NEED(DETECTED)` |
| Cycle | `GoalEvaluated`, `EntityStateUpdated` | `GOAL_EVALUATION(progress_delta, evidence)` |
| Need | `NeedUpdated` | `NEED(EVALUATED)` |
| Objective question | `ThoughtCreated` | `GOAL(USED_FOR_GOAL_DESCRIPTION)` |

## Acceptance criteria

1. Birth creates exactly one Goal `ASSIST_USER`, one EntityState and one Need per Entity.
2. An assistance interaction increments `progress_events` and creates GoalEvaluation with reference to the Observation.
3. After shutdown/restore, Goal and progress maintain the same IDs and consistent version.
4. “What is your goal?” responds only with the active Goal persisted.
5. A Need is audited, but does not result in an Action, ToolCall, Plan or Task.
6. VS-000–VS-003 remain green.

## Deliberate limits

There are no Planner, Scheduler, conversational creation of Goals, Tasks, external autonomy, notifications, tools or Genome evolution. VS-004 creates guiding state, not autonomous behavior.