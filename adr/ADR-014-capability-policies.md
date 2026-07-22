#ADR-014 — Persistent Policy Before External Capabilities

**Status:** accepted
**Date:** 2026-07-18

## Context

The capabilities SDK stabilized the executors' contract, but an executor should not decide whether an entity can act. Without its own layer, authorization, approval and limits would end up distributed across email adapters, files and calendars.

## Decision

Adopt `CapabilityPolicyService` as Kernel-owned boundary. The policy is persisted per entity and capability and produces immutable `PolicyEvaluation`. The decision is applied before preparation and reevaluated immediately before approved execution.

`CapabilityPolicy` is the source of truth for: principal, session, rule state, approval requirement, and daily limit. An executor receives only an already authorized operation; does not receive the policy or access the Mind state.

## Consequences

- Revoking a policy has effect even on requests that have already been approved.
- A denial is observable as `CapabilityResult(POLICY_DENIED)`.
- Introducing a new executor does not require adding authorization rules to the Kernel.
- The administrative configuration of policies remains outside this slice; defaults are created at the entity's birth.

## Rejected alternatives

1. **Check permissions within each executor.** Duplicates logic and creates bypasses.
2. **Use human approval only.** Does not cover quotas, session limits, or revocation.
3. **Allow when no policy exists.** Violates the fail-closed principle.

## Migration

Existing entities receive default policies during `ensure_ready`. No external capability is executed if the corresponding policy does not exist.