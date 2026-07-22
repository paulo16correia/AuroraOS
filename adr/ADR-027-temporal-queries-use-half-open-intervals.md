#ADR-027 - Temporal queries use half-open ranges

**Status:** Accepted
**Date:** 2026-07-19

## Decision

The World Model represents validity as `[valid_from, valid_to)`. The beginning and
inclusive; the end, when it exists, is exclusive.

## Justification

This convention avoids overlapping at the moment of transition between two
consecutive relationships. And it allows the Kernel to determine validity without asking
to the linguistic model for reasoning about time.

## Alternative rejected

- **Inclusive ending:** creates ambiguity when a relationship ends in the same
  the moment another one begins.