# Aurora Platform — 100: Implementation Order v1.0

**Type:** frozen implementation plan · **Depends on:** Architecture Freeze v1.0

## Objective

Prevent development by jumping between modules. Each step creates a testable foundation for the next; You don't start with Discord, email, browser or UI before there is a minimum Kernel, recoverable state and applied policies.

## Mandatory sequence

```text
0. Toolchain, repositories and contracts
1. Minimal kernel and lifecycle
2. State Manager, PostgreSQL e auditoria
3. Event Bus, outbox, idempotence and DLQ
4. Policy/Permission Engine e Vault abstraction
5. Mind Manager + Genome + Mind State/backup/restauro
6. Domain Model + Minimum Memory/Knowledge/World Model
7. Ciclo cognitivo, Attention, Working Memory, Decision e Planner
8. Capability Resolver + Tool Manager sandbox
9. A low-risk pilot application and end-to-end observation
10. API e UI de controlo/approvals/auditoria
11. Scheduler, Signals, Needs, Status and Maintenance
12. Applications adicionais e autonomia limitada por regra
```

## Gates per stage

| Step | It only moves forward when |
| --- | --- |
| 1–3 | lifecycle recovers, events are idempotent and auditing is verifiable. |
| 4–5 | permissions deny by default; isolated restoration passes; secrets do not appear in logs. |
| 6–7 | memory has provenance, cycle does not skip Decision/Policy and context expires. |
| 8–9 | capability does not couple to provider; each action generates Observation and reconciles `UNKNOWN`. |
| 10–12 | user controls memory/approvals; Automations are revocable, limited, and observable. |

## First vertical slice

A local conversation creates a `Event`, opens loop, reclaims empty/allowed memory, produces `Decision(RESPOND)`, stores audit, creates `Observation` from the response, and survives restart. Do not use external tools. Only then can a low-risk reading Application be added.

## Rules

- No step can introduce a later step dependency as a shortcut.
- Discovery that contradicts RFC/Law opens ADR and blocks only the affected scope.
- Each merge must keep the system booting, migrating and running the minimum set of tests.