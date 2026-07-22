# Aurora OS — RFC 010: Mind Master Map

**State:** Normative · **Depends on:** RFC 000–01

## Objective

Fix Aurora's architectural plan. Any new module MUST declare where it lives on this map, what data it reads/writes, what policies it applies and what events it emits.

## Component map

The map of this RFC is the end-to-end flow view. RFC 011 introduces the layered complementary view and is the reference for dependency boundaries.```mermaid
flowchart TB
U[User and external world] --> P[Perception and Gateway]
  P --> A[Attention System]
  A --> WM[Working Memory]
  WM --> CC[Cognitive Cycle]
subgraph M[Mind — governed persistent state]
    I[Self, Identity e Personality]
    ME[Memory, Experience e Beliefs]
    K[Knowledge Graph, Relationships e World Model]
    G[Goals, Plans, Tasks e Preferences]
    R[Attention, Working Memory, Reflections e Learning]
  end
  M <--> WM
  M <--> CC
  CC --> DE[Decision Engine]
  DE --> POL[Policy e Permissions]
  POL --> TO[Tool Orchestrator]
  TO --> EW[Email · Discord · Browser · GitHub · SSH · APIs]
  EW --> O[Observations]
  O --> CC
CC --> OUT[Response, report or confirmation request]
  SCH[Scheduler] --> P
  SCH --> CC
  VAULT[Vault] --> TO
  AUDIT[Audit Trail] --- CC
  AUDIT --- TO
```

## Platform hierarchy

```text
Aurora Platform
├─ Aurora Kernel governs lifecycle, state, events, permissions, resources and execution
├─ Aurora Mind brings together identity, memory, knowledge, beliefs, goals and cognition
└─ Aurora Applications realize capabilities through isolated connectors
```

Mind and Applications are managed subsystems; none assume Kernel responsibilities. RFC 045 specifies this control plane, and RFC 051 prevents Mind from being coupled to suppliers.

## Architecture and responsibilities

```text
Mind maintains persistent reality, identity, intentions and experiences
Cognitive Cycle orchestrates a unity of deliberation without skipping steps
Decision Engine chooses a permitted mode of action
Tool Orchestrator materializes an authorized action and observes the result
Scheduler creates temporal events; does not perform effects directly
```

## Structures and interfaces

```text
MindSnapshot
  snapshot_id, tenant_id, identity_ref, active_goal_refs[]
  memory_summary_ref, world_model_version, attention_state_ref, captured_at

Mind.read(scope, access_context) -> MindSnapshot
Mind.apply(change_set, policy_decision) -> MindSnapshot
ArchitectureRegistry.register(module_manifest) -> Registration
```

## Mandatory rules

1. No external modules write directly to Mind; sends a validated `ChangeSet`.
2. No module skips `Decision Engine` to call a tool.
3. The Scheduler only creates `Event`; a scheduled action goes through the complete cognitive cycle.
4. Data source, classification, and correlation accompany each pass between components.

## Limit and error cases

If Mind is unavailable, effective actions are blocked and new observations are in a durable queue. If Attention or Working Memory fails, the cycle ends in safe mode, without recovering “all memories” as an alternative.

## Justification

The map prevents connectors and models from becoming centers of authority. It also makes explicit that Mind is a state system, not a vector basis.

## Future expansions

Sub-Minds per domain, consented knowledge sharing, multimodal perception modules and scaling of independent components.