# Design 0019 — Persistent memory with provenance (step 6a)

**Status:** Implemented · **Date:** 2026-08-23
**Implements:** RFC 03 · **Baseline:** `docs/adr/0012-specification-baseline.md`
**Gate:** steps 6–7 — *memory has provenance*

## Naming

The domain type is `MemoryRecord`, not `Memory`. `System.Memory<T>` exists in every file that uses
`System`, and an ambiguous domain type is a trap for whoever reads this next.

## Rule 1 is the whole point

A memory without an origin and an access policy **is not persisted**. Both are refused at
`RecordAsync` before anything is written. This is the difference between a memory and a rumour, and
it is what the step 6–7 gate means by "memory has provenance".

## Inferred facts start as candidates

A fact stated by the person is `ACTIVE`. A fact the system inferred is `CANDIDATE`, and callers can
exclude candidates from results entirely. RFC 03 is explicit that candidates may guide questions and
suggestions, never high-impact actions; separating them at write time is what makes that
enforceable later rather than aspirational.

## Rule 2: access before ranking, structurally

`IMemoryRanker` is a separate interface and only ever receives records the caller is already
permitted to see. Access policy and sensitivity ceiling are applied in the query itself.

Making the ordering structural rather than a comment matters: a ranker that never sees a forbidden
record cannot leak one through a score, a snippet or a count.

`WORKING` memories are excluded from search entirely — the RFC calls them ephemeral and says they do
not enter lasting research.

## Rule 5: sensitive material needs the rule that permits it

Recording `CONFIDENTIAL` or `SECRET` material without a `SpecificRuleRef` is refused. The default is
that Aurora does not consolidate sensitive third-party data; permission is something a rule grants
explicitly, not something the absence of an objection implies.

## Rule 3: the owner's correction prevails

Once a person has corrected a memory, an automatic correction is refused with a reason. The system
does not quietly correct a human's correction back.

## Rule 4: retraction removes reach, not history

`ForgetAsync` marks the memory `RETRACTED`, removes it from search, and keeps the record and its
revision chain. The tombstone states the real scope in words the caller can repeat to a person,
because "deleted" would be a lie and "kept" alone would be alarming.

## Contradictions are kept, not resolved

Recording a fact that contradicts an active one marks **both** `DISPUTED`. Silently superseding one
would destroy the evidence that they ever disagreed — and which one is right is frequently not the
newer one.

## Absence is only asserted when it is known

Search returns `Confident`. When ranking fails, the service falls back to the structured result and
says so, rather than reporting an empty list. RFC 03's limit case is precise about this: an index
failure must not become a confident claim that nothing is remembered.

## Ranking

`LexicalMemoryRanker` scores term overlap, with confidence breaking ties so a confirmed fact
outranks an equally-matching guess. RFC 03 describes hybrid search with embeddings; this is the
structured half, and an honest lexical baseline beats a stub that pretends to understand meaning.
The embedding half is deferred.

## Tests

18 conformance tests covering both halves of rule 1, provenance retained, candidates versus stated
facts, candidate exclusion, sensitive material refused and then allowed with a rule, policy and
ceiling filtering, working memory excluded, ranking, degraded search, contradictions disputed,
owner correction prevailing, the revision hash chain, and forgetting.

## Deferred

Embeddings and hybrid search. Retention enforcement — `retention_until` is stored and nothing yet
expires on it. RFC 03's future expansions: scheduled consolidation, document import with consent,
retention by jurisdiction, shared memories, automatic obsolescence.

`memory.remember` and `memory.recall` stay frozen. They are capabilities, which belong to step 8,
and they will be re-founded on this model rather than on the note table they used before.

## Next

Step 6b: Knowledge Graph (RFC 04), then 6c: World Model (RFC 041).
