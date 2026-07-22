# Aurora OS — RFC 011: Cognitive layers and boundaries of responsibility

**Status:** Normative · **Depends on:** RFC 000, 010

## Objective

Organize Aurora OS by cognitive and operational layers rather than a flat list of components. Layers describe the direction of information flow and prohibit dependencies that destroy isolation, explainability, or security.

## Reference architecture```mermaid
flowchart TB
  subgraph EW[External World]
    D[Discord] & E[Email] & B[Browser] & GH[GitHub] & S[SSH] & C[Calendar] & F[Filesystem] & V[Voice] & X[APIs]
  end
  subgraph PL[Perception Layer]
    MP[Message Parser] & EP[Event Parser] & DP[Document Parser] & IP[Image Parser] & AP[Audio Parser] & SO[State Observer]
  end
  subgraph CL[Cognitive Layer]
    AT[Attention] --> WM[Working Memory]
    WM --> KR[Knowledge Retrieval]
    KR --> PLR[Planner]
    PLR --> DE[Decision Engine]
    DE --> RE[Reasoning and Deliberation]
    RE --> RF[Reflection] --> LE[Learning]
  end
  subgraph M[Mind — persistent state]
    SELF[Self] & ID[Identity] & PS[Personality] & LTM[Long-term Memory]
    KN[Knowledge] & EX[Experience] & GO[Goals] & BE[Beliefs]
    PR[Preferences] & RL[Relationships] & WMOD[World Model]
  end
  subgraph EL[Execution Layer]
    TM[Tool Manager] & PE[Permission Engine] & SC[Scheduler] & TE[Task Executor] & WF[Workflow Engine]
  end
  subgraph IF[Infrastructure]
    PG[Postgres] & NG[Graph Store] & RD[Redis] & VA[Vault] & OS[Object Storage] & LG[Logs/Audit] & EV[Event Bus] & DC[Containers] & MO[Monitoring]
  end
  EW --> PL --> CL
  CL <--> M
  CL --> EL --> EW
  SC --> PL
  EL <--> IF
  M <--> IF
  PL <--> IF
```

## Responsibilities

| Layer | Do | Doesn't |
| --- | --- | --- |
| External world | produces signals and receives effects | changes policies or Mind directly |
| Perception | normalizes, authenticates and classifies observations | decides objectives or takes actions |
| Cognition | selects context, deliberates, plans and decides; evaluates signs, needs, situation and resources | keeps secrets or bypasses permissions |
| Mind | maintains canonical, temporal and governed state | calls connectors directly |
| Execution | authorizes, schedules and materializes decisions | invents intention or facts |
| Infrastructure | persists, isolates, measures and recovers | defines product semantics |

## Structures and interfaces

```text
LayerEnvelope
  event_id, correlation_id, source_layer, target_layer
  payload_ref, schema_version, sensitivity, integrity, occurred_at

LayerContract
  layer, accepted_event_types[], emitted_event_types[]
  read_scopes[], write_scopes[], forbidden_dependencies[]

LayerRouter.dispatch(envelope) -> DeliveryResult
Architecture.validateDependency(source, target) -> ValidationResult
```

## Mandatory rules

1. Communication between layers MUST use versioned contracts and events/references; Side access to storage is prohibited except for a published interface.
2. Execution cannot write facts to Mind: it only creates `Observation` and results, which come back through the perception/cognitive cycle.
3. Mind cannot store secret values ​​or depend on the availability of a connector to preserve state.
4. The Scheduler belongs to execution, but triggers a `Event` in perception; never calls a tool directly.

## Limit and error cases

- Failure of the external world leaves observations/tasks pending, without corrupting the Mind.
- Failure of perception puts events in quarantine; Unnormalized data never enters cognition.
- Infrastructure failure blocks changes and effects, preserving degraded reading when safe.

## Justification

The layers make Aurora an architectural organism: inputs are sensed, state is maintained, decisions are made, and actions are executed across clear boundaries. This prevents a new connector from accidentally becoming a new mind.

## Future expansions

Real-time multimodal perception, security sublayers, isolated remote execution and federated Minds.