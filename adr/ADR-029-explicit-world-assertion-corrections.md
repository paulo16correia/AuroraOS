# ADR-029 - Corrections create new assertions and preserve disputes

**Status:** Accepted
**Date:** 2026-07-19

## Context

The VS-030 allows you to dispute a `WorldAssertion`, but a dispute only removes
operational confidence; does not determine the correct fact. The system must accept
a subsequent correction without making the old evidence invisible or overwritten.

## Decision

An explicit user correction creates a new `WorldAssertion(CURRENT)`.
This preserves the evidence of the correction and references the assertion `DISPUTED`
previous in `supersedes_assertion_ref`. The original assertion remains immutable
and disputed.

The fix requires a single `DISPUTED` assertion eligible for the subject and
predicate. Ambiguity, lack of dispute or conflict with a current relationship
to another object blocks writing.

## Consequences

- The audit maintains original fact, contestation and substitute fact.
- Operational queries only retrieve the new `CURRENT` relationship.
- The Kernel does not deduce denials, project exclusivity or hierarchy between
  sources.

## Rejected alternatives

- **Change the disputed assertion to a new current fact:** loses provenance.
- **Resolve any dispute with a positive sentence:** accepts ambiguity and
  allows incidental text to rewrite history.