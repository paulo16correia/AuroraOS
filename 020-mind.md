# Aurora OS — RFC 020: Mind — Cognitive State Model

**Status:** Normative · **Depends on:** RFC 000, 01, 010

## Objective

Set **Mind** as Aurora's canonical persistent state aggregate, managed by the Aurora Kernel. Mind is not a prompt or a single database; it is the contract that links identity, known reality, experience and intention. The memory, knowledge, goals, personality and learning RFCs flesh out their components without changing this contract.

## Composition

```text
Mind
├─ Self capabilities, limits, health and current work
├─ Identity who you are and how you communicate
├─ Memory/Experience what happened and what was learned
├─ Knowledge / World Model what is known about entities and relationships
├─ Beliefs/Preferences Revisible defaults and user choices
├─ Social, professional and resource relationships
├─ Goals / Plans / Tasks what you want to achieve
├─ Attention what deserves resources now
├─ Working Memory active, temporary and isolated context
├─ Reflection/Learning assessment and governed change
└─ Policies / Permissions what is allowed to do
```

## Data structures

```text
Mind
  id, tenant_id, status: INITIALIZING|ACTIVE|DEGRADED|PAUSED|RECOVERING|RETIRED
  self_model_id, identity_id, policy_set_version, world_model_version
  belief_ids[], preference_ids[], relationship_ids[]
  active_context_ids[], active_goal_ids[], active_task_ids[], last_consolidation_at
  created_at, updated_at

MindChangeSet
  id, mind_id, source: USER|CYCLE|TOOL|SCHEDULER|OPERATOR
  changes[], evidence_refs[], policy_decision_ids[]
  status: PROPOSED|VALIDATED|APPLIED|REJECTED|ROLLED_BACK
```

## Interfaces

```text
MindService.open(tenant_id) -> Mind
MindService.snapshot(mind_id, scope) -> MindSnapshot
MindService.propose(change_set) -> MindChangeSet
MindService.apply(change_set_id) -> Mind
MindService.pause(mind_id, actor) -> Mind
```

## Mandatory rules

1. Aurora persistent state MUST belong to a `Mind` and be subject to access control.
2. `MindChangeSet` MUST be atomic per aggregate or compensated for explicit changes; never partially silent.
3. `PAUSED` prevents new automatic actions and tasks, but allows inspection and authorized export.
4. Working Memory is not consolidated without RFC 024 and RFC 03; Temporary observation is not memory.

## Limit and error cases

- **Two operations change the same intention:** use optimistic version; a failure with conflict and is reevaluated.
- **Abort recovery:** `RECOVERING` reconciles tasks and tool calls before returning to `ACTIVE`.
- **Withdrawal from Mind:** export/delete according to retention, revoke connectors and prevent reactivation without explicit bootstrap.

## Justification

Naming Mind eliminates the “LLM + memory” false simplification. Architecture starts to treat context, reality, objectives and experience as parts of the same governed system.

## Future expansions

Personal and team minds, signed snapshots, branches for simulation and migration between installations.