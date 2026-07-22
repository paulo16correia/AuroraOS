# LAW-002 — Mind never communicates directly with tools

## Statement

Mind represents state; does not perform effects. Every intention that can use a tool runs through `Decision Engine → Policy/Permission Engine → Execution Layer → Tool Manager`.

## Application

```text
Mind state → Decision → authorization → Action/ToolCall → Tool Manager → connector
```

## Verifiable control

- Only Tool Manager can own connector manifests and vault leases.
- `MindService` does not expose network, shell, email or credentials methods.
- Audit must relate each `ToolCall` to `Decision`, policy and approval when required.

## Exceptions

They don't exist. A health consultation is an observation of infrastructure and not a call from Mind to a tool.

## Justification

Avoids business logic and external effects hidden within memory or knowledge models.