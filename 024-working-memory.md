# Aurora OS — RFC 024: Working Memory

**Status:** Normative · **Depends on:** RFC 03, 021, 023, 040

## Objective

Provide the temporary, isolated, and limited space where a cycle maintains drafts, selected context, intermediate results, and pending decisions. It's not a vector basis, it's not an endless conversation and it's not persistent memory by default.

## Architecture

```text
AttentionSet → ContextFrame (per cycle) → drafts/results/hypotheses
                                           │
                                           ▼
consolidation decision
                                  ├─ descartar
                                  ├─ anexar a auditoria
                                  └─ propor Memory/Experience
```

## Data structures

```text
WorkingMemory
  id, cycle_id, session_id, status: OPEN|SEALED|EXPIRED|DISCARDED
  capacity_tokens, capacity_items, sensitivity_ceiling, expires_at

WorkingItem
  id, working_memory_id, type: CONTEXT|DRAFT|HYPOTHESIS|RESULT|QUESTION
  payload_ref|payload_json, source_refs[], confidence, sensitivity
  created_at, expires_at, disposition: PENDING|DISCARD|AUDIT|CONSOLIDATE
```

## Interfaces

```text
WM.open(cycle_id, attention_set) -> WorkingMemory
WM.put(id, item) -> WorkingItem
WM.seal(id) -> WorkingMemory
WM.dispose(id, consolidation_decisions) -> DisposalReport
```

## Mandatory rules

1. Each Working Memory MUST belong to a cycle; Sharing between cycles requires explicit transfer of items.
2. It has TTL, capacity limits and rating ceiling; upon expiration, it is sealed and discarded/consolidated.
3. Hypotheses remain marked as such and may NOT become facts without the flow of RFC 03.
4. The content must not be exposed to the user as “internal reasoning”; Outputs are approved operational summaries.

## Limit and error cases

- **Capacity exhausted:** Attention removes the least useful item or cycle requests clarification; does not silently truncate sensitive data.
- **Reset:** retrieve only objects sealed in short-term encrypted storage; Open drafts may be discarded per policy.
- **Long session:** open successive frames with cited summaries, do not extend an unlimited frame.

## Justification

An explicit working memory allows handling a complex task without polluting lasting memory and limits the context exposure radius.

## Future expansions

Frames for multimodality, authorized debug snapshots, and per-task collaborative working memory.