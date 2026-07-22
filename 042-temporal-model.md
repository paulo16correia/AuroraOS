# Aurora OS — RFC 042: Temporal model and validity

**State:** Normative · **Depends on:** RFC 040–041

## Objective

Provide uniform semantics for time, recurrence, validity, chronology, and human reference (“yesterday”, “in three days”, “last week”). Time is part of the meaning of memory, belief, relationship, state and decision.

## Architecture and flow

```text
text/event + time zone + trust clock
                         │
                         ▼
temporal interpretation → normalized interval/occurrence
                         │
                         ▼
domain validity → scheduler → historical query “as of”
```

## Data structures

```text
TemporalExpression
  original_text, locale, reference_time, timezone
  resolved_kind: INSTANT|INTERVAL|RECURRENCE|RELATIVE|UNKNOWN
  start_at, end_at, recurrence_rule?, confidence

ValidityInterval
  valid_from, valid_to?, asserted_at, observed_at, expires_at?

TemporalPolicy
  default_timezone, daylight_saving_rule, clock_source
  retention_rules[], recurrence_limits[], ambiguity_policy
```

All persisted instants are UTC; the origin zone is saved for display and calculation. Ranges are semi-open `[start, end)` to avoid boundary overlap.

## Interfaces

```text
Time.parse(expression, reference_context) -> TemporalExpression
Time.now(trusted_source) -> Instant
Time.isValid(object, at) -> boolean
Time.queryAsOf(pattern, at) -> Result[]
```

## Mandatory rules

1. An event MUST have `occurred_at`; an assertion MUST separate occurrence, observation, and assertion when they differ.
2. Ambiguous expressions require locale/zone and may require clarification; do not assume a date when it implies action.
3. Recurrences have a materialization limit and policy for lost executions, according to RFC 026.
4. Customer time is not entrusted for approval, expiration or critical audit.

## Limit and error cases

- **“Next Friday” ambiguous:** present absolute date before scheduling/sending.
- **Time change:** use calendar library with IANA zone; Never add 24 hours manually to a local occurrence.
- **Different clock:** block signed/expiring actions until secure synchronization.
- **Undated fact:** allow `unknown`, do not invent a chronology.

## Justification

Without a temporal model, Aurora confuses current state with history, misses reminders, and cannot explain when something was valid. Temporality is a dimension of the World Model, not optional metadata.

## Future expansions

Business time, regional holidays, team calendars, causality between events and temporal simulation.