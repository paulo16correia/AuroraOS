# Design 0059 — Three divergences, written down

**Status:** Decided · **Date:** 2026-08-26
**Closes:** the remaining findings in `docs/reviews/rule-conformance-2026-08-26.md`

Each of these is a place where Aurora does not do what an RFC states, for a reason that holds. None
of them was written down, and an undocumented divergence is indistinguishable from an oversight —
which is precisely how the conformance pass had to be run in the first place.

## RFC 042 — Aurora parses no dates

RFC 042 states `Time.parse(expression, reference_context) -> TemporalExpression`, and neither exists.

**Aurora is MCP-first.** The thing on the other end of the protocol is a language model, and turning
"next Tuesday" into an instant is what it is for (RFC 045, RFC 10). Aurora receives explicit
ISO-8601 instants and a named zone. A second parser here would be a second interpretation of the
same sentence, disagreeing with the first in exactly the cases that matter — the ambiguous ones.

Rule 2's substance is met without it: the scheduler requires an explicit timezone, so Aurora never
assumes a date when one implies action. `ValidityInterval` is implemented as half-open
`[valid_from, valid_to)` columns throughout. `TemporalPolicy` exists as the scheduler's own
configuration rather than as a record.

**If a channel is ever added that hands Aurora raw text**, this stops being true and `Time.parse`
becomes necessary.

## RFC 045 rule 1 and RFC 09 rule 3 — one process, one identity

Both ask for separate service identities: "The Kernel, Mind, Applications, and MCP transport are
separate planes with their own service identities and permissions", and "All services MUST use their
own identities and authenticated communication; there is no shared superuser account."

Aurora is **one process on one machine**. The planes are namespaces, and the boundaries between them
are enforced by the architecture tests — LAW-002's reflection test is a stronger guarantee than a
credential would be, because a credential can be shared and a missing method cannot be called.

There is one bearer token for the agent and one operator session for the person, and those two are
genuinely separate: `docs/adr/0010` and `0011`. Issuing four more credentials that the same process
holds in the same address space would be theatre — it would look like least privilege and grant
nothing.

**This is the same shape of exception as `docs/adr/0012`** (SQLite rather than PostgreSQL), and it
should have been recorded the same way. If Aurora is ever split across processes, the rule applies in
full and this ADR is superseded.

## RFC 06 — the connector path is complete and dormant

`IToolManager` implements propose, authorize, dispatch, reconcile and per-tool secret leasing. No
`IToolConnector` ships, because Aurora reaches nothing outside this machine.

The registry itself is **not** dormant: an incident disables a tool through it (`docs/adr/0056`).
What is dormant is the call path, and it has no caller because there is nothing to call.

Two of the conformance findings were exactly this shape — `DescribeAsync` implemented and
unreachable, RFC 08's `TESTING` state declared and never entered — and both went unnoticed for as
long as they did because nothing said out loud that they were unused. So `DormantSurfaceTests` now
names every unimplemented seam with the reason it is empty, and fails when a new one appears:

- `IToolConnector` — Aurora is local-only and ships no external connector.
- `IEventConsumer` — events are consumed by the SSE stream, which pulls from the outbox by sequence;
  nothing in-process subscribes yet. This is the edge that would let the plugin registry raise an
  incident without a dependency cycle, and it is the natural next piece of work.
