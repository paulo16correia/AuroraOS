# ADR-025 - The World Model preserves history by ending relationships

**Status:** Accepted
**Date:** 2026-07-19

## Decision

When an observed relationship ceases to be in force, Aurora changes its status
to `HISTORICAL` and fills in `valid_to`. Does not eliminate the assertion, the evidence
nor the original provenance.

## Justification

Deleting a relationship mixes "is no longer current" with "never existed". Preserve the
timeline allows you to explain answers and prepare future queries `as_of`
without inventing a narrative based on loose messages.

## Rejected alternatives

- **Delete the relationship:** loses evidence and makes it impossible to explain the past.
- **Create a negative relation:** introduces a global negation semantics that
  the first World Model still cannot resolve it safely.