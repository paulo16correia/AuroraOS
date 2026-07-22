# Aurora OS — RFC 04: Knowledge Graph

**Status:** Normative · **Depends on:** RFC 03

## Objective

Represent entities, relationships and statements in a navigable, explainable and memory-coherent way. The graph accelerates relational questions; it does not replace the transactional source or validate facts in itself.

## Architecture

```text
Memory ACTIVE ──→ entity normalizer ──→ node/edge proposal
                         │                              │
                         ▼                              ▼
validation/provenance resolution
identity │
                         └────────────→ grafo versionado ←┘
Consultation → authorization → limited expansion → evidence → cited response
```

## Data structures

```text
Entity
  id, type, canonical_name, aliases[], attributes_json
  status: ACTIVE|MERGED|ARCHIVED, sensitivity, source_refs[]

Relation
  id, subject_id, predicate, object_id|literal_json
  qualifier_json, direction, confidence, source_memory_ids[]
  status: PROPOSED|ASSERTED|DISPUTED|RETRACTED
  valid_from, valid_to, asserted_at

EntityType / Predicate
  key, display_name, allowed_subject_types[], allowed_object_types[]
  cardinality: ONE|MANY, inverse_key, sensitivity_rule
```

Examples of predicates: `USES(Project, Technology)`, `OWNS(Person, Account)`, `DEPENDS_ON(Task, Task)`, `MENTIONED_IN(Entity, Document)`. Relationships without a source are `PROPOSED` and are not used as the sole basis of an action.

## Interfaces

```text
Graph.propose(memory_id) -> GraphChangeSet
Graph.query(pattern, depth<=3, access_context) -> Subgraph
Graph.merge(entity_a, entity_b, actor) -> MergeRecord
Graph.explain(relation_id) -> provenance[]
```

## Mandatory rules

1. Nodes and edges MUST have type; Free relationships in text are not allowed in the canonical graph.
2. Expansion depth is limited by order, budget and policy.
3. The graph MUST maintain temporal validity: a current relationship does not automatically eliminate the previous one.
4. Entity merger MUST be reversible via redirection registration.
5. `SECRET` data MUST not produce nodes searchable by name or vectors.

## Limit and error cases

- **Homonyms:** create separate entities until there is sufficient evidence for merger.
- **Improper cycle in dependencies:** reject change and return cycle chain.
- **Source withdrawn:** preserve the auditable edge, remove `ASSERTED` status and reevaluate dependents.
- **Graph unavailable:** use structured memory; report that relational research is degraded.

## Justification

A typed schema prevents language-generated relations from becoming arbitrary facts. Temporality allows us to answer “was” versus “is” without destroying history.

## Future expansions

Domain ontology, contact import, interactive visualization, approved rule reasoning and community detection.