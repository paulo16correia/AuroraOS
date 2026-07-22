# VS-030 - World Model: relations in dispute

## Objective

Allow an explicit user statement to challenge a current relationship
without deleting your history. The relationship changes to `DISPUTED` and is no longer used
as fact in current or temporal queries.

## Vertical scene

```text
WorldAssertion(CURRENT, Paulo -> Aurora)
       |
+-- "The information that Paulo works on the Aurora project is wrong."
                         |
                         v
WorldAssertion(DISPUTED, evidencia original + correcao)
                         |
                         v
World.query -> does not return the assertion as a confirmed fact
```

## Mandatory rules

1. A dispute only arises from an explicit declaration by the user.
2. `WorldAssertion` is preserved, with the dispute message added to
   `evidence_refs`; It is not erased or transformed into a global negation.
3. `DISPUTED` Assertions do not enter current, historical or `as_of` queries.
4. Repeating the same objection is idempotent: state, evidence and version not
   change again.
5. The system responds by ignorance as long as there is no new
   assertion confirmed; does not choose a version of facts based on popularity.

## Limit cases

- Contestation without corresponding current relation: do not create negative state.
- Two independent relations: disputing one does not affect the other.
- Dispute resolution and creation of replacement assertion are outside this
  slice; require evidence and their own cycle.

## Acceptance criteria

1. The challenge creates the auditable transition `CURRENT -> DISPUTED`.
2. The Event Bus issues `WorldAssertionDisputed`.
3. Current queries and `as_of` no longer return the disputed fact.
4. Repeating the challenge does not create new entities or change the version.