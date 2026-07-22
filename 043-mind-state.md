# Aurora OS — RFC 043: Mind State — serialization, recovery and continuity

**State:** Normative · **Depends on:** RFC 020, 024, 027–029, 040–042

## Objective

Set Aurora's serializable state in an instant so that an outage, migration, or restart resumes work in a secure and verifiable way. A Mind State is not a database dump: it is a coherent snapshot of aggregates and references, with temporal consistency.

## Architecture

```text
Mind + Self + World Model + cycles/tasks + scheduler + tools
                                │
                                ▼
consistency barrier
                                │
                                ▼
              MindStateSnapshot cifrado, versionado e assinado
                                │
             ┌──────────────────┴──────────────────┐
             ▼                                     ▼
restart/migration audit/authorized export
             │                                     │
             ▼                                     ▼
RECOVERING → reconciliation → ACTIVE vision written by ACL
```

## Data structures

```text
MindStateSnapshot
  id, mind_id, schema_version, captured_at, consistency_cursor
  identity_ref, personality_ref, self_ref, belief_refs[], preference_refs[]
  relationship_refs[], goal_refs[], active_task_refs[], plan_refs[]
  attention_state_ref, working_memory_refs[], world_model_version
  tool_state_refs[], scheduler_state_refs[], interaction_state_ref
  policy_set_version, health_ref, audit_anchor_hash, encryption_metadata
  status: CREATING|COMPLETE|VERIFIED|RESTORED|EXPIRED|CORRUPT

RecoveryPlan
  id, snapshot_id, target_environment, steps[]
  unresolved_tool_call_refs[], reconciliation_policy, status
  status: PLANNED|RUNNING|WAITING_RECONCILIATION|COMPLETED|FAILED
````tool_state_refs` only contain identifiers and states, never secrets. `working_memory_refs` are only included if the short retention policy allows; Opened items can be sealed, discarded, or converted to trade-in orders.

## Interfaces

```text
MindState.capture(mind_id, consistency_level) -> MindStateSnapshot
MindState.verify(snapshot_id) -> VerificationReport
MindState.restore(snapshot_id, environment) -> RecoveryPlan
MindState.export(snapshot_id, access_context) -> RedactedExport
```

## Mandatory rules

1. A snapshot MUST capture consistent versions or declare non-consistent components; never pretend atomicity that doesn't exist.
2. Restore MUST start Mind on `RECOVERING`, revoke temporary leases, and reconcile `ToolCall`/`Action` `UNKNOWN` before further actions.
3. Snapshots MUST be encrypted, authenticated, subject to retention, and tested with isolated restore.
4. User export applies third-party ACL, wording, and policies; Vault data is never exportable.
5. Emotional/interactional state is represented as temporary operational context, not as actual feeling claim.

## Limit and error cases

- **Corrupt snapshot:** mark `CORRUPT`, do not partially restore without explicit plan; use last `VERIFIED`.
- **Action was sending email on restart:** keep `UNKNOWN`, consult the provider and only then decide if there is a new action.
- **Scheme changed:** perform versioned migration or compatibility restoration; do not deserialize unknown fields as permissive.
- **One week offline:** reevaluate expired deadlines, credentials, schedules and beliefs before declaring `READY`.

## Justification

Persisting only “memories” does not restore an operational entity. Mind State preserves the continuity of identity, intention, focus, known world and pending work, without converting secrets or ephemeral reasoning into lasting data.

## Future expansions

Incremental snapshots, cross-region recovery, branching for simulation, cross-vendor migration, and cryptographic state attestation.