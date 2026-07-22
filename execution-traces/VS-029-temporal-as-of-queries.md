# VS-029 - Temporal World Model: as-of queries

## Objective

Consult a World Model list in an instant. The question is not
uses the object creation date: evaluates the range `[valid_from, valid_to)`
that the assertion itself declares.

## Vertical scene

```text
Assertion: Paulo --WORKS_ON_PROJECT--> Aurora
valid_from = 2026-07-17T10:00:00Z
valid_to   = 2026-07-18T10:00:00Z

"What project was Paulo working on on 2026-07-17T12:00:00+00:00?"
        |
        v
World.query(as_of=2026-07-17T12:00:00Z)
        |
        v
Aurora
```

## Mandatory rules

1. The query requires ISO 8601 date/time with offset or `Z`.
2. An assertion validates whether `valid_from <= as_of` and `as_of < valid_to`; if
   `valid_to` is null, the relationship remains valid.
3. The Kernel retrieves the assertions; the LLM client receives only returned facts through MCP
   and does not calculate intervals or fill in gaps.
4. No assertion validates at the time of request returns ignorance, never
   an assumption about present or past.
5. The trace records `AS_OF_RETRIEVED`, the instant consulted and the references
   used in the response.

## Limit cases

- Instant equal to `valid_to`: does not belong to the closed interval.
- Date without timezone or invalid: do not execute temporal query.
- More than one valid relation: return all as results, without choosing
  arbitrarily one.

## Acceptance criteria

1. A query in the middle of a historical range returns the correct assertion.
2. A query after `valid_to` does not use this assertion.
3. A query in the reactivation interval returns the current assertion.
4. Repeating the query does not change the persisted state.