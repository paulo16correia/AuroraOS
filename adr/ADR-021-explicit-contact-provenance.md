# ADR-021 — Explicit provenance for contacts

## Decision

Aurora's contact directory only learns through explicit user assertions. Each contact and group registers `provenance = USER_ASSERTION`.

## Justification

Resolving a person to the wrong recipient is an external execution risk. The language model and implicit inferences are not sufficient sources of truth for this decision.

## Consequences

The system can say that it doesn't know “John” instead of guessing. When there is a resolution, it is auditable, persistent and linked to a concrete Entity. Future conversational quality can evolve without weakening the security boundary.