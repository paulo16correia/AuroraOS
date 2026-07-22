# VS-028 - Temporal World Model: reactivation of relationships

## Objective

Allow reactivation of a historical relationship without changing or deleting its range
previous. The vertical case is: Paulo worked on the Aurora project, left it and more
Later he returned to the same project.

## Flow

```text
WorldAssertion(HISTORICAL, valid_from=A, valid_to=B)
       |
+-- "Paulo returned to the Aurora project."
                         |
                         v
WorldAssertion(CURRENT, valid_from=C, valid_to=null)
                         |
                         v
Query atual -> assertion C
Query historica -> assertion A-B
```

## Mandatory rules

1. Reactivating creates a new `WorldAssertion`; never changes the historical assertion
   back to `CURRENT`.
2. The new assertion has its own evidence, `valid_from` and identifier
   stable derived from the reactivation message.
3. There can only be one `CURRENT` relationship by combining subject, predicate and
   object. Repeating the same reactivation returns the current relationship, without duplication.
4. A reactivation with no previous historical relationship does not create a state by inference.
5. Posterior closure locates the current relationship by logical identity,
   not by a first generation identifier.

## Acceptance criteria

1. After shutting down and reactivating, there are two assertions: a `HISTORICAL` and
   a `CURRENT`.
2. The present query returns the new assertion; the passed query retains the
   previous assertion.
3. Repeating the reactivation phrase does not create a third assertion.
4. The trace and Event Bus register `WorldAssertionReactivated`.