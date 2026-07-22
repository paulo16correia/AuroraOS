# ADR-001 — Entity Runtime Context

**Status:** ACCEPT
**Date:** 2026-07-17
**RFCs affected:** 020, 039, 040, 043, 050; Execution Traces VS-000 and VS-001

## Context

VS-000 demonstrated a full cycle and persistence, but the trace was anchored to `session_id`. A session is ephemeral; cannot own the Mind, memory, history, or state of a persistent entity. This gap blocks proof of continuity after shutdown/restart.

## Decision

Enter `Entity` and `EntityRuntimeContext` as canonical contracts:

```text
Entity
 ├─ possui Genome, Mind State, Lifecycle e Life History
└─ contains Sessions
└─ contains Traces/Cycles
````entity_id` is required in all Kernel domain agreements, events, trace logs, audits, snapshots and commands. The Kernel creates/restores the runtime context before processing a Signal. Mind does not choose or change the owner of the entity.

## Consequences

- The execution now requires/assumes an explicit entity (`entity_001` only as a CLI demonstration value).
- Persistence must maintain Entity, lifecycle state and Mind State independently of sessions/processes.
- VS-001 becomes the **Resurrection Test**, before any external capability.

## Migration

Existing SQLite bases receive `entity_id` columns in events and traces, keeping old data with the value `legacy_unscoped` for reading only. New writes require valid `entity_id`. The API/CLI adds `--entity-id` and `--genome-id`.

## Rejected alternatives

- Using `session_id` as an identity: it loses continuity and confuses presence with existence.
- Save entity only in Memory: does not provide life cycle, state owner or deterministic restoration.