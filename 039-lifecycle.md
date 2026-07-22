# Aurora Platform — RFC 039: Instance Lifecycle

**State:** Normative · **Depends on:** RFC 011, 026–027, 033, 036, 043

## Objective

Specify the macro states of existence of an Aurora instance and the transitions allowed between boot, availability, deliberation, execution, maintenance, backup and termination.

## State machine

```text
CREATED → BOOTSTRAPPING → RECOVERING → READY
                                     │
                  ┌──────────────────┼──────────────────┐
                  ▼                  ▼                  ▼
              DELIBERATING       EXECUTING        MAINTAINING
                  │                  │                  │
                  └──────────────→ WAITING ←─────────────┘
                                     │
                                     ▼
                          PAUSED | BACKING_UP | UPDATING
                                     │
                                     ▼
                              SHUTTING_DOWN → STOPPED → RETIRED
```

## Structures and interfaces

```text
InstanceLifecycle
  instance_id, state, entered_at, reason, active_cycle_refs[]
  pending_action_refs[], last_verified_snapshot_ref, version

Lifecycle.transition(instance_id, target_state, reason) -> InstanceLifecycle
Lifecycle.prepareShutdown(instance_id) -> ShutdownPlan
Lifecycle.resume(instance_id) -> InstanceLifecycle
```

## Mandatory rules

1. You cannot enter `STOPPED` without verified snapshot or audited emergency reason.
2. `UPDATING` and `BACKING_UP` drain idempotent work and mark incomplete external calls for reconciliation.
3. `PAUSED` prevents new effects; `WAITING` preserves cycles dependent on data, time or approval.
4. Only the Kernel can perform transitions; Mind can propose, never change the life cycle directly.

## Limit cases, justification and expansions

Abrupt failure is observed as `RECOVERING`, not `READY`; the Kernel reconciles before reactivating. The lifecycle makes Aurora's operational existence controllable, recoverable, and observable. In future it supports hibernation, hot migration and multiple instances.