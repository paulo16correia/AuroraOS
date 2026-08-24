# Design 0020 — Knowledge Graph (step 6b)

**Status:** Implemented · **Date:** 2026-08-23
**Implements:** RFC 04 · **Depends on:** `docs/adr/0019-memory.md`

## Rule 1: a phrase is not a fact

A predicate that is not registered in the schema cannot enter the graph. `ProposeAsync` returns a
rejection naming the predicate rather than inventing a type for it.

This is the rule that stops language-generated relations becoming arbitrary facts, and it only
works if it is absolute: one convenient exception and the canonical graph is no longer canonical.

Relations derived from a memory that is not active and sourced stay `PROPOSED`, which RFC 04 says is
never the sole basis of an action.

## Rule 3, and an interaction worth recording

Temporal validity works: a new assertion over a `ONE`-cardinality predicate closes the previous one
instead of deleting it, so *was* and *is* are both answerable.

Building it surfaced a genuine conflict between two RFCs. RFC 03 marks memories with the same
subject and predicate but different objects as contradictory, so a person moving city produced two
`DISPUTED` memories and the graph refused to assert either. The move was being treated as a
disagreement.

The fix belongs in memory: contradictions now require the validity windows to **overlap**. Someone
who lived in one city and then another is not contradicting themselves. Two claims about the same
period still dispute each other, which is what the rule was for.

## Rule 4: merges redirect, never destroy

`MergeAsync` marks the merged entity `MERGED` with a redirection to the survivor and writes a merge
record. `UnmergeAsync` reverses it from that record and refuses to run twice.

Reversibility is only possible because nothing is deleted — an entity that was destroyed cannot be
un-merged, whatever the record says.

## Rule 5: SECRET is not discoverable

A `SECRET` entity is excluded from name and type searches entirely, while remaining reachable by an
id the caller already holds. The rule is about discoverability: it stops the graph turning a secret
into something findable by browsing, without pretending the entity does not exist for a caller who
legitimately has its identifier.

Sensitivity is also checked against the caller's ceiling on every hop, not just on the seed, so a
walk cannot reach a classified neighbour through an unclassified one.

## Rule 2: bounded expansion

Depth is clamped to three hops. Passing a larger number silently clamps rather than failing,
because the cap is a safety limit and not a contract the caller can be wrong about.

## Limit cases

**Homonyms.** When more than one active entity shares a type and name, the graph does not pick. It
creates a separate entity and reports the ambiguity, exactly as RFC 04 asks: separate entities until
there is evidence enough to merge.

**Cycles.** `AssertRelationAsync` walks the predicate forward before writing, and a change that
would close a loop over an acyclic predicate is refused **with the chain**. "There is a cycle" is
not actionable; `t3 -> t1 -> t2 -> t3` is.

**Withdrawn source.** `OnSourceWithdrawnAsync` preserves the edge and removes `ASSERTED`. A fact
whose evidence was withdrawn is no longer a fact, but pretending it never existed would erase the
reasoning trail — which is precisely what provenance exists to keep.

**Graph unavailable.** A query that fails at the database returns a `Degraded` subgraph saying so,
rather than an empty one that would read as "no relations exist".

## An entity-to-entity path was needed

`ProposeAsync` builds edges from memories, whose objects are literals. RFC 04's own examples
(`DEPENDS_ON(Task, Task)`) are entity-to-entity, and without that path the cycle rule was
unreachable code. `AssertRelationAsync` adds it, and enforces subject **and** object types against
the schema.

## Tests

16 conformance tests: unregistered predicate refused, registered one producing a typed edge,
unconfirmed memory staying proposed, depth clamped, temporal succession answering both *was* and
*is*, merge and un-merge, double un-merge refused, `SECRET` invisible by name but reachable by id,
ceiling enforced, homonyms kept separate and reported, cycle refused with its chain, forbidden
object type refused, withdrawn source de-asserting the edge, and explain returning provenance.

## Deferred

Embeddings and vector search — rule 5 also forbids `SECRET` from producing vectors, which becomes
enforceable when vectors exist. RFC 04's future expansions: domain ontology, contact import,
visualisation, approved-rule reasoning, community detection. Inverse predicates are carried in the
schema and not yet materialised.

## Next

Step 6c: World Model (RFC 041).
