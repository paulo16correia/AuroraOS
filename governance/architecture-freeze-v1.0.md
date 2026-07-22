# Aurora Platform — Architecture Freeze v1.0

**Effective on:** 2026-07-17
**Frozen baseline:** commit `a720961` and respective fixes approved in revision v1.0

## Decision

Cognitive Architecture v1.0 is frozen. No new concept, module, domain object, or layer RFCs will be created until implementation reveals a material flaw that cannot be resolved within existing contracts.

## What stays frozen

- `Aurora Platform → Aurora Kernel / Aurora Mind / Aurora Applications` hierarchy.
- Constitution, Laws and boundaries of authority.
- Domain model, lifecycle contracts and Event Bus communication.
- Separation between mission, objective, plan, task, decision, action and observation.
- Kernel, Mind and Applications responsibilities.

## Changes allowed

1. Editorial corrections without semantic changes.
2. Compatible clarifications that do not add state, effect, public interface or dependency.
3. Implementations, tests, operation documentation and compliant examples.
4. ADRs for required changes, each with impact, migration, compatibility and approval.

## Changes locked

- New cognitive subsystems, new types of canonical state or new exceptions to the Laws.
- Increased autonomy, permissions or tool surfaces without ADR and security review.
- Change an RFC directly to resolve implementation disagreement.

## Criteria for reopening the architecture

A proposal only reopens the freeze if it demonstrates: (a) violation of Law/Constitution, (b) ambiguity that blocks a reference implementation, (c) security/recovery failure, or (d) unavoidable incompatibility between frozen contracts. The request starts with an ADR, not a new RFC.