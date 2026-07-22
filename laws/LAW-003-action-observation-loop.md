# LAW-003 — Every action generates an observation

## Statement

Every authorized `Action` must end with at least one `Observation` result: success, failure, abort, or unknown state. The cycle is not considered completed without reflection on this observation.

## Application

```text
Action → ToolCall → resultado externo → Observation → Reflection → proposta de aprendizagem
```

## Verifiable control

- `Action` cannot transition to `OBSERVED` without `Observation` connected.
- Timeouts create `Observation(status=UNKNOWN)`, not “presumed success”.
- The Scheduler and UI must expose unobserved tasks as pending reconciliation.

## Justification

Closes the loop between intention and the real world. Without observation, Aurora doesn't know if she acted, if she failed, or if she should learn.