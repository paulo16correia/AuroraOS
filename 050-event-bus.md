# Aurora Platform — RFC 050: Event Bus

**Status:** Normative · **Depends on:** LAW-007, RFC 040, 045

## Objective

Define the Event Bus as the communication backbone between Kernel, Mind and Applications. The Bus carries domain facts and control signals; does not carry secrets or replace canonical storage.

## Architecture

```text
Producer → Transactional Outbox → Event Bus → idempotent subscription → consumer
                 │                   │                 │
                 ▼                   ▼                 ▼
retention audit Dead-letter queue

MemoryCreated → Knowledge / Planner / Reflection / UI / Logs
```

## Data structures

```text
DomainEvent
  event_id, type, schema_version, producer, occurred_at
  correlation_id, causation_id, aggregate_ref, payload_ref|payload_json
  sensitivity, idempotency_key?, integrity_hash

Subscription
  id, consumer, event_types[], filter_ref, delivery_mode
  checkpoint, status: ACTIVE|PAUSED|FAILED, retry_policy

Delivery
  event_id, subscription_id, attempt, delivered_at
  status: PENDING|ACKED|RETRYING|DEAD_LETTER|SKIPPED
```

## Interfaces

```text
Bus.publish(event) -> DomainEvent
Bus.subscribe(subscription) -> Subscription
Bus.ack(delivery_id) -> Delivery
Bus.replay(subscription_id, cursor) -> Delivery[]
```

## Mandatory rules

1. Producers write state and outbox in the same transaction; subsequent publication is re-executable.
2. Consumers are idempotent, schema check and do not assume global order between aggregates.
3. Big/sensitive data uses authoritative references, not open payloads.
4. Repeated failures go to auditable dead queue, never silently discarded.
5. Bus does not allow subscribers to obtain permissions from the producer.

## Limit and error cases

- **Duplicate event:** consumer ignores by `event_id`/idempotence key.
- **New scheme:** incompatible consumer pauses with diagnosis; does not interpret unknown fields permissively.
- **Bus unavailable:** services only accept operations whose outbox can be persisted; dependent external effects are postponed.

## Justification

Events allow `MemoryCreated` to update index, UI and audit without direct coupling. Bus creates a causality story that supports recovery and debugging.

## Future expansions

Partitions, data class retention, cross-facility federation, and isolated analytic event streams.