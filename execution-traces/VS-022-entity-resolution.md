# VS-022 — Entity resolution

## Objective

Allow an Aurora entity to resolve persistent user references, without asking for an email address in each statement.

```text
Explicit user assertion
        ↓
Contact / ContactGroup persistido
        ↓
Request: “send an email to João”
        ↓
RecipientResolution
        ↓
EmailDraft(validado)
        ↓
Policy → Approval → Executor
```

## Objects

`Contact` contains displayed name, standard aliases, email addresses, origin, status and version. `ContactGroup` contains aliases and the explicit list of members. `RecipientResolution` records the original reference, resolved addresses, source objects, status and reason.

## Mandatory rules

- Only an explicit statement from the user creates or updates contacts.
- LLM never creates, corrects or chooses contacts.
- “João is joao@example.com” and “Pedro has the email pedro@example.com” are accepted statements.
- “My team includes ana@example.com, bruno@example.com” creates or updates the persistent group.
- Unmatched references are `UNRESOLVED`; multiple matches get `AMBIGUOUS`. Both block `EmailDraft` before approval.
- Contacts, groups and resolutions belong to the Entity, survive restarts and are included in the Mind snapshot.

## Limits

There is no automatic import of external contacts, internet searching or identity inference. Integration with Google Contacts, synchronization, per-contact permissions, and conversational disambiguation are future expansions.