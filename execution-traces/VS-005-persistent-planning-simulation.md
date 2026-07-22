# Aurora OS — Execution Trace Specification: VS-005 Persistent Planning Layer

**Status:** Authorized by ADR-004
**Use case:** retrieve user context → evaluate Goal/Need → propose persistent plan → restore → describe pending work

## Objective

Demonstrate controlled deliberation without action. The Entity must be able to imagine a help sequence, persist that proposal, and retrieve it after restart, maintaining the boundary `Desire → Goal → Need → Plan → Action`: VS-005 ends at `Plan`.

## Normative flow

```text
User assertion: "My name is Paulo and I like programming Python."
  → Memory(LIKES_PROGRAMMING_LANGUAGE, USER_ASSERTION)
  → Need(UNDERSTAND_USER) avaliada

User: "Como poderias ajudar-me melhor?"
  → Signal → Attention → MemoryRetrieval(planning_context) → Context
  → Goal(ASSIST_USER, ACTIVE) + Need(UNDERSTAND_USER)
  → PLANNING
  → PlanCreated(ASSISTANCE_PLAN, PROPOSED, SIMULATION_ONLY)
  → Thought/Decision(RESPOND) → Response
  → Observation → Reflection → Memory
→ no Action/Task/ToolCall

shutdown → MindState(plan_refs[]) → restore

User: "What goal did you have pending?"
→ PlanLoaded → Goal + Plan used to respond
```

## Minimum contracts

```text
Plan
  plan_id: "plan:{entity_id}:assist-user"
  entity_id
  type: ASSISTANCE_PLAN
  goal_ref: Goal(ASSIST_USER)
  created_from_need: Need(UNDERSTAND_USER)
  status: PROPOSED|REVIEWED|APPROVED|REJECTED|COMPLETED
  steps: PlanStep[]
  basis_memory_refs[]
  execution_mode: SIMULATION_ONLY
  version, created_at, updated_at

PlanStep
  sequence: 1..n
  action: ASK_CLARIFICATION|PROVIDE_RECOMMENDATION
  execution_mode: SIMULATION_ONLY
  status: PENDING
```

## Mandatory rules

1. A Plan can only be born from an explicit request in this slice; startup, scheduler and isolated Need never create it.
2. `goal_ref`, `created_from_need` and `basis_memory_refs` must belong to the active Entity.
3. The first proposal always uses `PROPOSED` and `SIMULATION_ONLY`; any further transition is prohibited in VS-005.
4. The response about the proposal or pending work is derived from the Plan persisted by the Kernel.
5. `PlanCreated` cannot coexist, in the same trace, with `ActionCreated`, `TaskCreated`, `CapabilityRequested` or `CapabilityExecuted`.
6. Shutdown snapshot includes `plan_refs`; restoration does not generate a new proposal or duplicate the object.

## Events, traces and errors

| Situation | Event | Trace | Error/behavior |
| --- | --- | --- | --- |
| Explicit request eligible | `PlanCreated` | `PLANNING(PROPOSED)`, `PLAN_CREATED` | Persist a single proposal per Entity/Goal. |
| Already existing plan | `PlanLoaded` | `PLANNING(LOADED)` | Reuse, don't duplicate. |
| Later consultation | `PlanLoaded` | `PLAN(LOADED)` | Expose only persisted state. |
| Goal/Need missing | none Plan | Kernel controlled error | Never fabricate references. |
| Order without capacity | no effect | `CAPABILITY(NOT_REQUIRED)` | No tools are consulted. |

## Acceptance criteria

1. Create Roberto, remember Paulo's preference and ask “How could you help me better?” creates a single Plan `PROPOSED`.
2. The Plan has two PlanSteps defined, in `PENDING` and `SIMULATION_ONLY`.
3. The trace contains Goal, Need, `PLANNING` and `PLAN_CREATED`; does not contain external execution.
4. After shutdown/restore, “What goal did you have?” describes the active Goal and the persisted proposed Plan.
5. VS-000 to VS-004 remain green.

## Deliberate limits

There are no persistent Desire, human approval, plan review, Tasks, Scheduler, Capability Registry, tools, simulated actions or learning from the plan. VS-005 represents possible future; does not change the world.