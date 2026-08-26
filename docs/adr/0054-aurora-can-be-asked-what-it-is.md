# Design 0054 — Aurora can be asked what it is

**Status:** Implemented · **Date:** 2026-08-26
**Closes:** the finding recorded in `docs/adr/0053` — `DescribeAsync` had no caller

## The gap

`ISelfModel.DescribeAsync` was implemented, tested, and reachable from nowhere. RFC 027 rule 3
defines `Self.describe(access_context) -> SafeSelfDescription` and the implementation satisfied it,
but no MCP tool and no API endpoint exposed it. Aurora had a considered answer to "what are you"
and no way to be asked.

That also left LAW-008 half-exercised. Its trace control — `SELF_MODEL(USED_FOR_SELF_DESCRIPTION)` —
can only fire when somebody describes Aurora, and nobody could.

## `aurora_self`

RFC 10 names **identity** among the capability families the MCP surface exposes, and deliberately
does not hardcode tool names. `aurora_self` returns the `SafeSelfDescription` and nothing else:
operational state, what Aurora can and cannot do right now, its health and when that was observed,
and how many cognitive cycles are running.

The requester recorded in the trace is **Aurora's own view of who is asking**, taken from
`IPrincipalAccessor`, not a name the caller supplies. A caller-chosen requester would be a caller
writing Aurora's audit log.

## It does not run the cognitive cycle, and that is deliberate

`aurora_execute` runs the full cycle because it may commit an effect, and `aurora_review` runs it
because a briefing is a claim about what happened rather than a query result. A self description is
neither: it reads one persisted row and returns a projection of it, the same shape of operation as
`aurora_catalog` and `aurora_cycle`. Running a cycle to answer it would record a decision nobody
made.

## What the test asserts

The wire shape, field by field. `SafeSelfDescription` exists precisely because there is nowhere in
it to put a secret, a hostname or a credential identifier — so a test that only checked a couple of
values would not notice a field being added later. It also checks that what Aurora says it can do
comes from the capability registry rather than from an assumption.

## A note on the run that looked like a regression

Two consecutive full suite runs reported 28 and 33 minutes against a normal 12 seconds. Nothing was
wrong with the code: a second run had been started while the first was still going, and the two
competed for a nearly-full disk. Recorded because the obvious next move — bisecting the sandbox
health check, which really does touch the filesystem on every health read — would have been
several hours spent on a machine's load average.
