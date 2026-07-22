# LAW-004 — No memory is born in isolation

## Statement

A persistent memory must be related to at least one contextual anchor: entity, goal, conversation, observation, date/interval, tool, document or other memory. Records without sufficient relationships remain temporary candidates or are discarded.

## Application

```text
Memory candidate → minimum anchors → MemoryLink/Edge + provenance → Memory ACTIVE
```

## Verifiable control

- Validator requires `source_refs` and at least one typed anchor.
- `MemoryLink` and graph relations hold reason/evidence, not just vector similarity.
- The consolidation process reports orphaned candidates for review or expiration.

## Justification

Relations make a memory retrievable, explicable and temporally situated. A single sentence is too weak to guide future decisions.