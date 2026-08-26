# Design 0058 — The Mind and the unit of work

**Status:** Implemented · **Date:** 2026-08-26
**Closes:** RFC 020 rules 1 and 2, RFC 02's `WorkItem`, RFC 036 rule 3

## Two aggregates that were referenced and never built

**RFC 020's `Mind`.** Every component the Mind is supposed to own existed as its own service. The
aggregate that owns them did not, so `MindChangeSet` had nowhere to apply and `MindService` had
nothing to open. LAW-001 covered the spirit — nothing is persisted without provenance — for memories
and beliefs, which have their own guarded write paths. The Mind's own fields had no write path,
because there was no Mind.

**RFC 02's `WorkItem`.** `cognitive_cycle.work_item_id` and `tool_call.work_item_id` had carried a
column referencing it for a long time. What the column held was a subject reference:
`conversation/{ref}` from the pilot, `review/{client}` from the review application, and
`mcp/{action_id}` from the dispatcher — the same value for every call of the same capability. "The
cycles of this work item" looked like a question the column could answer and was not.

## The Mind holds what nothing else owns

RFC 020 lists `belief_ids[]`, `preference_ids[]`, `relationship_ids[]`, `active_goal_ids[]` on the
aggregate. Those are **not** stored here. Each is owned by the service that writes it, and copying
the ids onto this record would create a second answer to "what is active" that goes stale the moment
either side changes.

For the same reason, a change set may only move the Mind's own fields — which self model and identity
are current, which policy and world versions are in force, when consolidation last ran. Routing a
memory through here would be a second way into Mind, and a second way in is the thing LAW-001 exists
to prevent.

Rule 2's "atomic per aggregate, never partially silent" is one transaction over both tables: the
Mind's row and the change set's status move together or neither moves. The new state is built whole
before anything is written, so a field that cannot be applied is found while the Mind is still
untouched. A failure rolls back and marks the set `ROLLED_BACK` with the reason, rather than the
failure being visible only as an exception somebody logged.

Validation refuses a set with no evidence, one naming a field the Mind does not have, one setting a
field to nothing, and one that changes the same field twice — which of the two won would depend on
the order they were listed in, and that is not a decision anybody made.

## The work item is what a repeated request joins

Rule 1: at most one active work item per idempotency key. The second arrival returns the first
rather than erroring, because the right answer to a repeated request is the work already in flight.
"One active" and not "one ever": asking the same thing tomorrow is new work.

`WAITING_APPROVAL` counts as active. Treating it as finished would let a second identical request
start beside the one already waiting for a person to answer.

The three cycle entry points now open a work item and pass its id, so `CycleIngress.WorkItemId`
holds an identity and `IngressRef` finally holds what arrived.

## And one line of RFC 036

Rule 3: "preserves reference to the effective Genome in Mind State **and Life History**." Mind State
had it. An episode did not, so it could not be read against the version of Aurora that produced it —
and an episode from an earlier genome is an episode of a slightly different entity.
