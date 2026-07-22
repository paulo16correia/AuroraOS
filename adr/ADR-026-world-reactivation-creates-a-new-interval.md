# ADR-026 - Reactivation creates a new time interval

**Status:** Accepted
**Date:** 2026-07-19

## Decision

A reactivation creates a new `WorldAssertion(CURRENT)` with a new start of
validity. The previous `HISTORICAL` assertion remains immutable.

## Justification

A relationship that ends and comes back into existence represents two factual intervals.
Reusing the same object would erase the discontinuity and falsify queries
about the state of the world in each period.

## Alternative rejected

- **Reopen the historical assertion:** loses the interval in which the relationship does not
  was active and prevents a correct chronology.