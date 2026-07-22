# ADR-024 - World Assertions are evidence-oriented

**Status:** Accepted
**Date:** 2026-07-19

## Context

Textual memories are insufficient to represent operational reality:
do not allow us to safely ask about relationships, explain the origin of a
fact or distinguish an observation from an inference.

## Decision

Enter `WorldAssertion` as an immutable and persisted object. Each assertion
stores subject, predicate, object, validity, provenance and references of
evidence. The first predicate is `WORKS_ON_PROJECT`, created solely by
an explicit assertive message from the user.

## Consequences

- Queries can be answered from structured and auditable relationships.
- The LLM only receives retrieved facts and has no writing authority.
- The evolution to graph, rich temporality, conflicts and entity resolution
  maintains the same evidence contract.

## Rejected alternatives

- **Use only message vector search:** does not provide predicate,
  validity or structured provenance.
- **Allow LLM to extract and persist any fact:** proposed mix
  unreliable with canonically auditable state.