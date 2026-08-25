# Design 0030 — The operator API (RFC 10)

**Status:** Implemented · **Date:** 2026-08-25
**Implements:** `docs/10-api.md`
**Step:** 10a of `docs/100-implementation-order.md`

## Two doors, not one

MCP is the surface the model talks to. This is the surface a person talks to. They are kept apart
deliberately, and not merely for tidiness: the endpoints here are how the owner decides what Aurora
may do, corrects what it believes, and inspects what it did. An agent holding the MCP door must not
thereby hold the controls meant to govern it.

The consequence is visible in the routing. `/metrics` and `/sessions/revoke` were already operator
endpoints rather than tools for exactly this reason; `/v1/approvals/{id}/decide` now joins them.
Both surfaces sit behind the same loopback and bearer guard, so this is a separation of authority
within one trusted host, not a security boundary between two hosts.

## What the rules forced

**Rule 1 — write commands are repeatable.** Every write goes through `ApiIdempotency.RunAsync`,
which reuses the Kernel's existing idempotency ledger rather than adding a second one. A missing
`Idempotency-Key` is a 400 rather than a silently non-idempotent write. The same key with a
different body is a 409, because replaying the stored answer would be answering a question nobody
asked.

**Rule 3 — the server applies authorization.** `GET /v1/memories` passes the caller's access context
into `IMemoryService.SearchAsync`, and `GET /v1/stream` passes a sensitivity ceiling into the bus
read. Neither hands the client everything to filter locally; that is not filtering, it is disclosure
with extra steps.

**Rule 4 — existence is itself disclosure.** A memory outside the caller's reach answers **404, not
403**. A 403 would confirm the memory is there, which is the fact being protected. The same applies
to a goal owned by someone else. This is asserted directly in
`tests/Aurora.Tests/Integration/ApiSurfaceTests.cs`, including the negative assertion that the
answer is *not* 403 — otherwise a later refactor could "improve" the error code and quietly undo it.

For the same reason, an event above the stream's ceiling is omitted entirely rather than delivered
redacted. A redacted entry still says *something classified happened at this moment*.

## A goal arrives as a request, not an instruction

RFC 10 says `POST /v1/goals` creates a **DRAFT** goal, and that turned out to be load-bearing.
`IPlanner.CreateAsync` builds a goal *and* its first plan, and refuses a plan with no tasks — so
routing the endpoint through it would have meant either callers supplying their own task
decomposition, or a 500 for any well-specified goal.

Neither is right, and the reason is not mechanical. A goal posted from outside is something the
person wants. Whether and how Aurora pursues it is a decision Aurora has to make, through the
cognitive cycle. So `IPlanner.DraftAsync` was added: it records the intention in DRAFT with no plan,
and planning stays a separate act. The endpoint accepts no task list at all.

## One wire contract

`AuroraJson` already declared that every Aurora contract is snake_case, but the ASP.NET Core host
had its own options and served `/metrics` in camelCase — not by decision, by default. Minimal-API
binding and responses are now configured from `AuroraJson.Apply`, so the shape a client parses does
not depend on which code path answered.

This changed `/metrics` from `executionsByOutcome` to `executions_by_outcome`. It is a breaking
change to an endpoint no shipped client consumes, made now rather than after one does.

## Known limitation: the stream drains and closes

`GET /v1/stream` emits every committed event after the caller's cursor and then ends the response,
rather than holding the connection open for events that have not happened yet. Resume-by-cursor —
what RFC 10 actually requires of the stream — works: the client keeps the last `id` it saw and asks
for what follows, so a dropped connection costs a reconnect and not a gap.

What is missing is live push, which needs the Event Bus to signal a waiting reader instead of the
endpoint polling for one. It is recorded here rather than implemented behind a poll loop, because a
poll loop would look live and would not be.

## The stream's ceiling is PRIVATE, not the owner's full reach

A deliberate read can reach CONFIDENTIAL; the stream stops at PRIVATE. A stream is a standing
subscription that keeps delivering long after the person stopped watching it, so it is held one
class below what an explicit request can reach. Classified material is read through the endpoint
that asks for it by name.
