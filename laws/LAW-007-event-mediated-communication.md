# LAW-007 — Event-Mediated Communication

## Statement

Platform components communicate state changes and asynchronous reactions via the Event Bus. Direct calls are only allowed for synchronous queries published by contract and cannot create hidden cascading effects.

## Verifiable control

- Each producer declares events; each consumer declares subscriptions and schema version.
- Events have `event_id`, `correlation_id`, producer, date, classification and idempotency key when applicable.
- Consumers are idempotent and register delivery/processing.

## Exceptions

Restricted read queries, health checks and Kernel commands via authenticated interface. Even in these cases, resulting changes publish events.

## Justification

The bus reduces coupling, makes reactions observable, and allows UI, audit, reflection, and other consumers to react without hidden dependencies.