# Design 0013 — Event Bus, outbox and dead-letter queue (step 3)

**Status:** Implemented · **Date:** 2026-08-23
**Implements:** RFC 050, LAW-007 · **Baseline:** `docs/adr/0012-specification-baseline.md`
**Implementation order:** step 3 of `docs/100-implementation-order.md`

## Scope

Step 3 of the frozen order: Event Bus, transactional outbox, idempotence and dead-letter queue.
Idempotence already existed from the first phase and attaches here rather than being rebuilt.

In-process and single-node, which is what step 3 asks for. The properties that matter at this stage
are structural, not distributed.

## Conformance to RFC 050

**Data structures.** `DomainEvent`, `Subscription` and `Delivery` carry the fields the RFC lists.
Classification uses the `PUBLIC|PRIVATE|CONFIDENTIAL|SECRET` vocabulary of RFC 03.

**Interfaces.** `publish`, `subscribe`, `ack` and `replay` are present. `publish` goes through the
outbox even when a producer has no other state to commit, so rule 1 holds on every path.

**Rule 1 — state and outbox in one transaction.** `BeginAsync` hands the producer a scope; the
producer writes its own state and its events into it and commits once. A scope disposed without
committing rolls both back — an event can never describe a change that did not happen, and a
committed change cannot lose its event. Fan-out is re-executable through a unique index on
`(event_id, subscription_id)`: repeating publication after a crash cannot create a second delivery.

**Rule 2 — idempotent consumers, no assumed global order.** Delivery is at-least-once and the
interface says so. A settled delivery is never handed over again. A subscription receives only its
declared types.

**Rule 3 — sensitive data by reference.** An event carries exactly one of `payload_json` or
`payload_ref`, and `CONFIDENTIAL` or `SECRET` must use the reference. The bus carries facts; it does
not carry sensitive content and does not replace canonical storage.

**Rule 4 — nothing silently discarded.** Failures retry to the subscription's ceiling and then land
in an auditable dead-letter queue. A consumer that throws is treated as a retry rather than losing
the event.

**Rule 5 — no permission inheritance.** Subscribers receive events, never the producer's authority.
Nothing in the delivery path consults or conveys a principal.

## Error cases from the RFC

**Duplicate event.** The unique delivery index means a consumer sees an event once per
subscription; consumers remain responsible for their own idempotence by `event_id`.

**New schema.** An event whose `schema_version` exceeds what the subscription declares pauses that
subscription with a diagnosis naming both versions. The checkpoint does not advance and the event
waits. The consumer never interprets unknown fields permissively.

Resuming needed a decision the RFC does not make. Re-registering a subscription lifts the pause
**only** when the incoming `max_schema_version` is higher than the stored one — the pause is cleared
exactly when its cause is addressed. Any other re-registration leaves the status alone, so
re-subscribing can never quietly clear a `FAILED` state.

**Bus unavailable.** Not applicable in-process: the outbox is the same database as the state, so an
operation that can persist its state can persist its event. It becomes real when the bus moves out
of process, and is recorded as deferred.

## Head-of-line behaviour

A retry stops the pump for that subscription rather than skipping ahead, so a stream is not
reordered around an unsettled event. Dead-lettering is terminal and the subscription moves past it:
one poisonous event must not wedge everything behind it forever. Both are asserted by tests.

## LAW-007

Producers declare event types; consumers declare subscriptions with a schema version. Every event
carries `event_id`, `correlation_id`, producer, timestamp, classification and an integrity hash, and
an event without a correlation id is refused. Deliveries and processing are recorded.

## Tests

19 conformance tests, grouped by the rule or error case each one exercises: committed and rolled-back
scopes, re-executable fan-out, no redelivery after acknowledgement, type filtering, sensitive
payload refusal in both classes, exactly-one-payload-form, the LAW-007 field set, correlation id
required, dead-lettering after repeated failure, a throwing consumer retried, the stream moving on
after a dead letter, schema pause with diagnosis, a paused subscription resuming after upgrade, and
replay from a cursor.

## Deferred

From the RFC's own future expansions: partitions, retention by data class, cross-facility federation
and isolated analytic streams. Also out-of-process transport with its "bus unavailable" semantics,
and `filter_ref` evaluation — the field is carried but no filter language is defined yet.

## Next

Step 4: Vault abstraction alongside the existing policy engine.
