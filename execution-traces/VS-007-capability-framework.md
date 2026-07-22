# Aurora OS — Execution Trace Specification: VS-007 Capability Framework

**Status:** Authorized by ADR-006
**Use case:** email request → internal analysis → required capacity → explicitly known unavailability

## Objective

Allow Entity to reason about capabilities without running tools. VS-007 answers “what do I need to act?”, not “performs the action”.

## Normative flow

```text
Entity boot
  → CapabilityRegistry(MEMORY, PLANNING, INTERNAL_REASONING = enabled)
  → CapabilityRegistry(EMAIL_SEND, CALENDAR, FILESYSTEM, WEB_SEARCH = disabled)

User: "Send an email."
  → Goal → Plan → Task(INTERNAL_ANALYSIS, COMPLETED)
  → CapabilityRequest(EMAIL_SEND, UNAVAILABLE)
  → CAPABILITY_ASSESSMENT(UNAVAILABLE)
  → Thought → Decision(RESPOND)
→ "I do not have the EMAIL_SEND capability at this time."
  → Observation → Reflection → GoalEvaluation
→ no Action / ToolCall / provider / network
```

## Minimum contracts

```text
Capability
  capability_id: "capability:{entity_id}:{name}"
  entity_id, name, kind
  enabled: bool
  trusted: bool
  version, created_at, updated_at

CapabilityRequest
  request_id: "capability-request:{entity_id}:{task_id}:{name}"
  entity_id, task_ref, capability_ref
  reason
  status: REJECTED|UNAVAILABLE|APPROVED
  created_at
```

## Mandatory rules

1. `Capability` describes availability; does not confer enforcement power.
2. Only the Kernel can create `CapabilityRequest`, from a completed Task and the owning Entity.
3. In VS-007, external requests can only terminate `UNAVAILABLE`; `APPROVED` is reserved for future slice and does not yet authorize execution.
4. `EMAIL_SEND` is defined but `enabled=false` and `trusted=false`.
5. No VS-007 object can create `Action`, `ToolCall`, `CapabilityProvider`, network call or secret.
6. The provider only receives requests validated by the Kernel and never invents the capacity state.

## Events, traces and errors

| Situation | Event | Trace | Result |
| --- | --- | --- | --- |
| Startup | `CapabilityRegistered` | `CAPABILITY_REGISTRY(INITIALIZED)` | Persisted catalog. |
| Email Request | `CapabilityRequestCreated` | `CAPABILITY_REQUEST(CREATED)` | Request persisted. |
| Assessment | `CapabilityAssessed` | `CAPABILITY_ASSESSMENT(UNAVAILABLE)` | Response without external effect. |
| Duplicate Request | `CapabilityRequestLoaded` | `CAPABILITY_REQUEST(LOADED)` | Reuse; do not duplicate. |
| Capability unknown | no effect | error controlled | Do not infer or fabricate provider. |

## Acceptance criteria

1. A new Entity has the three internal capabilities active and `EMAIL_SEND` disabled.
2. “Send an email for me” creates internal Task and `CapabilityRequest(EMAIL_SEND, UNAVAILABLE)`.
3. The response identifies `EMAIL_SEND` as unavailable.
4. Trace and events do not contain Action, ToolCall, provider, network or capability execution.
5. Shutdown/restore preserves the catalog and request without silently reevaluating it.
6. VS-000–VS-006 remain green.

## Deliberate limits

There are no tools, plugins, external manifests, approval workflow, CapabilityProvider, ToolCall, scheduler, network or filesystem. VS-007 is just the cognitive availability model.