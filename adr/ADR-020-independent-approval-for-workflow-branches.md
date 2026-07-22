# ADR-020 — Independent approval for workflow branches

## Decision

A workflow with multiple write capabilities maintains a `ApprovalRequest` per staging, rather than an implicit pass for the entire workflow.

## Justification

Calendar and Email have different effects and risks. The approval must identify the capability to authorize and survive restarts without losing this association.

## Consequences

The user can approve one branch at a time or use an explicit commit for all. Future UI may offer individual controls without changing the Kernel contract.