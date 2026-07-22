# Aurora Platform — Execution Trace Specification: VS-001 Resurrection Test

**Status:** Authorized by ADR-001
**Use case:** create entity → cycle → shutdown → restart → restore → continue

## Objective

Prove that Aurora owns state beyond an execution or session. VS-001 does not introduce tools, external LLM, or vector knowledge. It only implements Entity Runtime Context, persistent lifecycle, minimal Mind State and idempotent recovery.

## Normative flow

```text
CreateEntity(entity_001, assistant-v1)
  → EntityCreated → EntityRuntimeContextLoaded
→ VS-000("Hello") → Mind State persisted
  → ShutdownEntity → SnapshotVerified → EntityStateChanged(STOPPED)
  → processo termina
  → StartEntity(entity_001) → SnapshotLoaded → EntityStateChanged(READY)
→ VS-000("Who am I?") → response based on entity context
```

## Minimum contracts

```text
Entity
  entity_id, genome_id, created_at, lifecycle_state, mind_state_version

EntityRuntimeContext
  entity_id, genome_id, lifecycle_state, active_session_id?, snapshot_ref
  restored_at?, trace_id
```

All objects created by the cycle carry `entity_id`. The required relationship is `Entity → Session → Trace`; a session can expire without changing the Entity.

## Steps and events

| Step | Persistence | Event |
| --- | --- | --- |
| Create | `Entity(CREATED)` and initial Mind State | `EntityCreated` |
| Start | lifecycle `RECOVERING → READY` | `EntityRuntimeLoaded`, `EntityStateChanged` |
| Execute message | VS-000 with `entity_id` | enriched VS-000 events |
| Close | snapshot/Mind State + lifecycle `STOPPED` | `EntityShutdown`, `EntityStateChanged` |
| Restore | load Entity/Mind State and reconcile | `EntityRestored`, `EntityStateChanged` |
| Continue | new Signal in a new Session | `SignalReceived` with same `entity_id` |

## Acceptance criteria

1. The first run creates Entity and Memory anchored to Entity, Session, Signal and Observation.
2. Shutdown persists a verifiable Mind State; no open cycle is silently lost.
3. A new process recovers the same `entity_id`, `genome_id`, memory and history without depending on the previous Session.
4. All events/traces from both processes show the same `entity_id` and different sessions.
5. “Who am I?” returns a deterministic response that identifies the Entity, does not invent consciousness or biography.
6. Repeating start/shutdown is idempotent and auditable.

## Failures

- Missing/corrupted snapshot: lifecycle becomes `RECOVERING`/`FAILED`; does not enter `READY`.
- `entity_id` unknown in restore mode: safe error without creating entity implicitly.
- Incompatible Mind State: apply approved migration or abort with diagnosis.