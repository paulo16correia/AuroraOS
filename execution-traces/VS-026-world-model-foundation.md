# VS-026 - World Model: explicit relationships with provenance

## Objective

Materialize the first behavior of RFC 041: Aurora stops saving
just text about a conversation and saves a structured statement
about a relationship in the world. The slice covers an explicit statement of work
in project and a later query, even after restarting the Entity.

## Vertical scene

```text
User: "Paulo works on the Aurora project."
        |
        v
Perception -> WorldAssertion(CURRENT, USER_ASSERTION)
        |
        v
Persistencia + Event + Trace
        |
        v
Shutdown / restore
        |
        v
User: "What project does Paulo work on?"
        |
        v
World.query -> read-only linguistic context -> response
```

## Contract

`WorldAssertion` represents an evident relationship, not a sentence:

```text
assertion_id, entity_id
subject_ref, subject_label
predicate
object_ref, object_label
evidence_refs[], confidence, provenance
observed_at, asserted_at, valid_from, valid_to
status: CURRENT|HISTORICAL|DISPUTED|RETRACTED
version
```

In VS-026, the only materialized predicate is `WORKS_ON_PROJECT`.
`subject_ref` and `object_ref` are canonical local identifiers; the labels
are separated to make the answer readable without confusing text with
internal identity.

## Mandatory rules

1. Only an explicit statement from the user can create a `WorldAssertion`.
   LLM does not create or alter World Assertions.
2. The entire assertion preserves the original message in `evidence_refs` and the
   provenance `USER_ASSERTION`.
3. A query without a corresponding assertion responds as unknown; absence
   data does not prove the absence of the relationship.
4. The model only receives facts retrieved by the Kernel as read-only context.
5. The assertion belongs to an Entity and survives shutdown/restore.
6. Correction, fusion of entities, contradictions and inference are outside this
   slice; they must receive their own cycles and ADRs.

## Error cases

- Incomplete or ambiguous sentence: do not create an assertion.
- Repetition of the same statement: update the existing assertion in a
  idempotent, without creating a second equivalent relationship.
- Unmatched person query: answer that there is no evidence
  registered, without inventing a project.

## Acceptance criteria

1. The assertion creates `WorldAssertion(CURRENT)` and outputs `WorldAssertionCreated`.
2. The trace has `WORLD_MODEL` stages for writing and recovery.
3. The later response only uses the retrieved assertion.
4. Shutdown and restore preserve the reference in `MindState`.
5. The regression suite remains green.