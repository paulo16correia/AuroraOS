# Aurora OS — RFC 027: Self — operational self-awareness

**Status:** Normative · **Depends on:** RFC 011, 020, 040

## Objective

Define **Self** as the operating model that Aurora maintains about itself: active identity, installed capabilities, effective permissions, limits, resources, health, work in progress, and recent operational history. Self does not mean human consciousness, subjective experience or autonomous authority.

## Architecture

```text
Identity + Personality + Capability Registry + Policy + Health + Work state
                                 │
                                 ▼
                       SelfModel versionado
                                 │
            ┌────────────────────┼─────────────────────┐
            ▼                    ▼                     ▼
Decision Engine UI/Diagnostic Limits Explanation
```

## Data structures

```text
SelfModel
  id, mind_id, identity_ref, personality_ref, version
  capability_snapshot_ref, permission_snapshot_ref, resource_snapshot_ref
  operational_state: BOOTING|READY|BUSY|WAITING|DEGRADED|PAUSED|RECOVERING
  active_cycle_ids[], active_task_ids[], current_focus_ref
  health_summary_ref, recent_activity_refs[], observed_at

CapabilitySnapshot
  id, available_tools[], enabled_capabilities[], disabled_capabilities[]
  limits_json, provider_statuses[], captured_at

ResourceSnapshot
  cpu_budget, memory_budget, storage_budget, queue_depth, model_budget
  network_status, maintenance_window, captured_at
```

## Interfaces

```text
Self.refresh(mind_id) -> SelfModel
Self.describe(access_context) -> SafeSelfDescription
Self.can(capability, context) -> CapabilityAssessment
Self.pause(actor, reason) -> SelfModel
```

## Mandatory rules

1. Decisions that propose a tool MUST consult the Self to confirm available capacity and operational limit.
2. Self MUST separate “installed capacity”, “permission granted” and “currently safe action”; one does not imply the other.
3. `Self.describe` only exposes a secure view, without secrets, sensitive topology or credential identifiers.
4. Health status is observed, dated and revocable; is not presumed healthy due to lack of warning.

## Limit and error cases

- **Connector installed but revoked:** appears as unavailable capacity; the plan is blocked/replanned.
- **Outdated state:** the execution revalidates permissions and health, it does not rely only on the snapshot.
- **Recovery after restart:** Self enters `RECOVERING` until tasks and `UNKNOWN` calls are reconciled.

## Justification

An agent who doesn't know what he can do creates wrong promises and failed attempts. Self allows Aurora to say “I can prepare, but not send” based on verifiable status.

## Future expansions

Budgets per user, distributed capacity, assisted self-diagnosis and daily operational reports.