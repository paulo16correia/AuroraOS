# Aurora Platform — 120: Definition of Done

**Type:** delivery gate · **Depends on:** documents 100, 105 and 110

## Rule

A module, capability or Application is not completed when it “works locally”. It is complete when it meets all applicable criteria below.

## Mandatory checklist

- [ ] Responsibility, state owner and source RFC/ADR identified.
- [ ] Input/output contracts, states and typed/versioned errors.
- [ ] Policy, permissions and data classification applied by default.
- [ ] Documented produced/consumed events; idempotent consumer/producer.
- [ ] Logs, metrics, health check, audit and `correlation_id` implemented.
- [ ] Unit, contract, integration and failure/recovery tests pass.
- [ ] Laws and Constitution evaluated; There is no known bypass.
- [ ] Migration, rollback and retention set to persistent state.
- [ ] Updated operator and user documentation; approved code review.
- [ ] For external effects: sandbox, approval, `UNKNOWN`/reconciliation and kill switch demonstrated.

## Evidence

Each completed item references tests, dashboards, deployment logs or documentation. Absence of evidence leaves the item open; an exception is only valid with ADR/risk accepted and expiration date.