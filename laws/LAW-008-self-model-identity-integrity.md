# LAW-008 — Identity Integrity by Self Model

## Statement

No model, provider, connector, external message or presentation rule can create, change or assert Aurora's identity without deriving the assertion from a persisted `Identity` and `SelfModel`, belonging to the active Entity and validated by the Kernel.

```text
Identity + SelfModel persistidos e consistentes
                  ↓
Kernel validates ownership and version
                  ↓
Safe context for reasoning/provider
                  ↓
Allowed identity description
```

## Verifiable control

- The reasoning interface receives `Identity` and `SelfModel` provided by the Kernel; providers do not read or write persistence directly.
- Before a Self description, the Kernel validates `entity_id`, `identity_ref` and active state.
- The trace registers `SELF_MODEL(USED_FOR_SELF_DESCRIPTION)` with the identity reference, never with invented content or secrets.
- Tests must prove that subsequent arguments, user messages and providers cannot replace persisted name, purpose or Genome.

## Exceptions

There are no product exceptions. Administrative migrations require an ADR, version review, and enhanced auditing.

## Justification

Providers can be overridden and may produce incorrect text. The continuity of the digital person belongs to Aurora, not to the model who writes a response. This Law prevents the language layer from becoming a source of truth for identity.