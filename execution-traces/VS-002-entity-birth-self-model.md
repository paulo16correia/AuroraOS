# Aurora OS — Execution Trace Specification: VS-002 Entity Birth + Self Model

**Status:** Authorized by frozen baseline (RFC 027 and RFC 040)
**Use case:** born with minimal identity → run → close → restore → describe yourself

## Objective

Give the Entity a persistent and auditable representation of itself. VS-002 creates an initial `Identity` and `SelfModel` at Entity birth — or, for existing VS-001 entities, in an explicit, audited initialization migration. The answer to self-identification questions MUST be derived from these persisted objects.

This slice does not implement adaptive personality, beliefs, preferences, relationships, goals, learning or external capabilities.

## Normative flow

```text
CreateEntity(Roberto, assistant-v1)
  → EntityCreated
→ CreateIdentity(name=Roberto, purpose=help the user)
  → CreateSelfModel(identity_ref, genome=assistant-v1, state=READY)
  → SelfModelInitialized
→ VS-000("Hello")
  → ShutdownEntity → MindState(snapshot + self_ref)
  → processo termina
  → StartEntity(Roberto) → Identity/SelfModel carregados
  → SelfModelLoaded
→ VS-000("Who are you?")
→ "I'm Roberto. I was created as a support entity. My initial purpose is to help the user."
```

## Minimum contracts

```text
Identity
  identity_id: "identity:{entity_id}"
  entity_id, name, purpose, genome_id
  locale: "pt-PT", profile_version: 1
  status: ACTIVE, created_at

SelfModel
  self_model_id: "self:{entity_id}"
  entity_id, identity_ref, genome_id, version: 1
  operational_state: READY
  capability_snapshot_ref?: null
  permission_snapshot_ref?: null
  resource_snapshot_ref?: null
  current_focus_ref?: null
  created_at, observed_at
````

Identity` is the source of truth for name and purpose. `SelfModel` references this identity and describes the operational state. Entity continues to own both; Session and Trace never own the Self.

## Events, tracing and persistence

| Step | Persistence | Event | Trace |
| --- | --- | --- | --- |
| Birth | `Entity`, `Identity`, `SelfModel` | `EntityCreated`, `SelfModelInitialized` | `ENTITY(CREATED)`, `SELF_MODEL(INITIALIZED)` |
| Normal start | reading of `Identity` and `SelfModel` | `SelfModelLoaded` | `SELF_MODEL(LOADED)` |
| Closing | `MindState` includes Self references | `EntityShutdown` | `ENTITY_RUNTIME(STOPPED)` |
| Restoration | same references and version | `EntityRestored`, `SelfModelLoaded` | `ENTITY(RESTORED)`, `SELF_MODEL(LOADED)` |
| Self-Identification | no field invented | `ThoughtCreated`, `ResponseProduced` | `SELF_MODEL(USED_FOR_SELF_DESCRIPTION)` |

All objects, events and trace entries MUST carry the same `entity_id` in the cycle.

## Mandatory rules

1. The birth of an Entity MUST create exactly one active `Identity` and one initial `SelfModel`.
2. A later startup may NEVER replace name, purpose, or Genome with command-line arguments; carries persisted state.
3. Absence of `Identity` or `SelfModel` in an existing Entity activates an audited compatibility initialization; does not create a second Entity.
4. Questions like “Who are you?” MUST use persisted `Identity` and `SelfModel`, not the `entity_id` received in the message.
5. `SelfModel` does not grant capabilities, permissions or autonomy. Empty references mean unavailability, not implied authorization.
6. The description returned to the user is secure: it does not include secrets, local paths, topology, credential IDs, or unnecessary internal details.

## Acceptance criteria

1. Create `Roberto` with `assistant-v1` persists `Identity` and `SelfModel` from version 1.
2. “Who are you?” responds with the saved name, nature and purpose, in European Portuguese.
3. After shutdown and restoration in a new Session, the response is identical and uses the same Identity/SelfModel IDs.
4. The trace contains `SELF_MODEL(INITIALIZED|LOADED|USED_FOR_SELF_DESCRIPTION)` and associated events.
5. Repeating the start is idempotent: it does not duplicate Identity or alter the existing Self.
6. The VS-000 and VS-001 set remains green.

## Failures and edge cases

- **Identity missing, Self present:** block description and repair only by approved migration; the current compatibility slice only initializes both when both are missing.
- **Self missing, Identity present:** create a Self that references the existing Identity and emit a compatibility event.- **Incompatible data:** keep the Entity in `RECOVERING`; Do not respond with invented information.
- **Empty name/purpose:** reject explicit birth; in the legacy path use `entity_id` as the name and the baseline purpose documented.

## Justification

VS-001 demonstrated process continuity. VS-002 demonstrates identity continuity: the restored entity doesn't just know that a technical key exists; queries a persistent, limited, and verifiable representation of who you are. This creates the basis for later adding Personality, Beliefs, Preferences, Relationships, Goals and Learning without confusing them with the initial identity.