# ADR-028 - Disputes preserve evidence without inference

**Status:** Accepted
**Date:** 2026-07-19

## Decision

An explicit challenge moves the assertion to `DISPUTED`. Aurora preserves
the two messages as evidence, but it does not treat the relation as current,
historical or valid at a specific moment.

## Justification

A fix says that the assertion is not secure, but does not automatically prove it
which is the correct fact. Inferring a negation or choosing a version would hide the
uncertainty and would create a false source of truth.

## Alternative rejected

- **Deleting the assertion:** destroys auditability and makes review impossible.