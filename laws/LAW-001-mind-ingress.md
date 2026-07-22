# LAW-001 — Nothing enters Mind directly

## Statement

No external data, model output, import, user text, or tool output can create or change canonical Mind state without traversing Perception, Classification, Provenance Assessment, and validated `MindChangeSet`.

## Application

```text
source → perception → classification → observation/candidate → evaluation → MindChangeSet → Mind
```

## Verifiable control

- Mind writing API only accepts `MindChangeSet` with `source_refs` and policy decision.
- Connectors and models do not receive write credentials to canonical storage.
- Tests must reject a `Memory`, `Belief`, `Preference` or `WorldAssertion` without provenance.

## Exceptions

There are no product exceptions. Administrative Bootstrap uses the same flow, with `OPERATOR` origin and hardened auditing.

## Justification

Prevents junk memory, injection by external content and consolidation of hallucinations as a permanent state.