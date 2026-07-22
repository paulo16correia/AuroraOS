# VS-031 - World Model: explicit correction of disputed relationships

## Objective

Allow the user to replace a `DISPUTED` relationship with a new relationship
evidenced, without deleting, reopening or changing the contested assertion. This slice
only treats `WORKS_ON_PROJECT`, when the message explicitly states the
fact corrected.

## Vertical scene

```text
WorldAssertion(DISPUTED, Paulo -> Aurora)
       |
+-- "The correct information is that Paulo works on the Nexus project."
                         |
                         v
WorldAssertion(DISPUTED, Paulo -> Aurora)  <- evidencia preservada
WorldAssertion(CURRENT,  Paulo -> Nexus)   <- nova evidencia + supersedes ref
                         |
                         v
World.query -> "Paulo works on the Nexus project."
```

## Entry contract

```text
The correct information is that <person> works on the project <project>.
```

The sentence must be an explicit statement by the user. Neither an LLM nor a
inference, nor an ambiguous message can create the replacement assertion.

## Mandatory rules

1. The fix is only eligible if there is exactly one `DISPUTED` assertion
   for the same person and predicate.
2. The disputed assertion never changes status, version, validity or evidence.
3. The new assertion has `status = CURRENT`, evidence of the correction message
   and `supersedes_assertion_ref` pointing to the disputed assertion.
4. If a `CURRENT` assertion already exists for the same person and project, the
   operation is idempotent: it does not create a third assertion.
5. A current assertion for another project is not terminated by implication;
   requires an explicit future transition.
6. Zero or multiple eligible disputed assertions result in no
   written and an auditable result in the trace.
7. The Event Bus issues `WorldAssertionCorrected` only when it creates the new
   assertion.

## Flow and audit

```text
Input -> Perception -> WorldModel.correction
-> validate a disputed assertion
      -> criar WorldAssertion(CURRENT)
      -> persistir -> evento -> trace -> query futura
````

WORLD_MODEL` registers `CORRECTED`, `CURRENT_ALREADY`,
`CORRECTION_NOT_ELIGIBLE` or `CORRECTION_CONFLICT_WITH_CURRENT`. The result
does not depend on the private reasoning of the language model.

## Limit cases

- **No prior dispute:** the form of correction does not create a relationship.
- **Two disputes for the same person:** the result is ambiguous; there is no writing.
- **Repetition of the correction:** the existing `CURRENT` relationship is reused.
- **Restore:** both assertions belong to MindState and survive the
  restart.

## Acceptance criteria

1. A valid fix leaves the original assertion `DISPUTED`.
2. Creates a distinct `CURRENT` assertion, with `supersedes_assertion_ref`.
3. The current query returns only the new project.
4. Repetition does not create duplicates.
5. Event, trace and persistence allow us to reconstruct the decision.