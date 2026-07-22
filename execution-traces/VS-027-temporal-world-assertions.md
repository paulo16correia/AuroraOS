# VS-027 - Temporal World Model: transitions to historical

## Objective

Implement the first temporal transition of the World Model defined in RFC 042.
A current `WORKS_ON_PROJECT` relationship can be terminated by an assertion
explicit from the user. The evidence remains; only its validity changes.

## Vertical scene

```text
"Paulo works on the Aurora project."
       |
       v
WorldAssertion(CURRENT, valid_to=null)
       |
       v
"Paulo left the Aurora project."
       |
       v
WorldAssertion(HISTORICAL, valid_to=<message instant>)
       |
       v
"What project did Paulo work on?"
       |
       v
World.query(as_of=historic) -> response with evidence
```

## Mandatory rules

1. The transition can only be initiated by an explicit assertive message from the
   user; LLMs and tools do not have canonical relationships.
2. `valid_to` is defined only once and the assertion goes from `CURRENT` to
   `HISTORICAL`. The object is not deleted.
3. The message that terminates the relationship is added to `evidence_refs`.
4. A second closing statement is idempotent: it does not change again
   `valid_to`, nor does it create duplicates.
5. Current queries ignore historical assertions; historical queries use
   exclusively `HISTORICAL` assertions retrieved by the Kernel.

## Limit cases

- No corresponding current relationship: do not create negative state or infer
  that the person never worked on the project.
- Two conflicting current relationships: only terminate the relationship explicitly
  identified; conciliation between projects is for a future VS.
- Reactivation: out of scope. A new statement of work creates a new
  assertion with new validity, without rewriting history.

## Acceptance criteria

1. The current relationship transitions to `HISTORICAL` with `valid_to` persisted.
2. The trace and the Event Bus record the temporal transition.
3. The historical query responds based on the persisted assertion.
4. Repeating the closing statement maintains the same end date.
5. Restoration preserves the historical assertion and its validity.