# Aurora OS — RFC 04: Grafo de conhecimento

**Estado:** Normativo · **Depende de:** RFC 03

## Objetivo

Representar entidades, relações e afirmações de forma navegável, explicável e coerente com a memória. O grafo acelera perguntas relacionais; não substitui a fonte transacional nem valida factos por si só.

## Arquitetura

```text
Memory ACTIVE ──→ normalizador de entidades ──→ proposta de nó/aresta
                         │                              │
                         ▼                              ▼
                    resolução de                 validação/proveniência
                    identidade                           │
                         └────────────→ grafo versionado ←┘
Consulta → autorização → expansão limitada → evidência → resposta citada
```

## Estruturas de dados

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

Exemplos de predicados: `USES(Project, Technology)`, `OWNS(Person, Account)`, `DEPENDS_ON(Task, Task)`, `MENTIONED_IN(Entity, Document)`. Relações sem fonte ficam `PROPOSED` e não são usadas como base única de uma ação.

## Interfaces

```text
Graph.propose(memory_id) -> GraphChangeSet
Graph.query(pattern, depth<=3, access_context) -> Subgraph
Graph.merge(entity_a, entity_b, actor) -> MergeRecord
Graph.explain(relation_id) -> provenance[]
```

## Regras obrigatórias

1. Nós e arestas DEVEM possuir tipo; relações livres em texto não são permitidas no grafo canónico.
2. A profundidade de expansão tem limite por pedido, orçamento e política.
3. O grafo DEVE guardar validade temporal: uma relação atual não elimina automaticamente a anterior.
4. Fusão de entidades DEVE ser reversível através de registo de redirecionamento.
5. Dados `SECRET` não DEVEM produzir nós pesquisáveis por nome ou vetores.

## Casos limite e erro

- **Homónimos:** criar entidades separadas até existir evidência suficiente para fusão.
- **Ciclo indevido em dependências:** rejeitar mudança e devolver cadeia de ciclo.
- **Fonte retraída:** preservar a aresta auditável, retirar-lhe estatuto de `ASSERTED` e reavaliar dependentes.
- **Grafo indisponível:** usar memória estruturada; informar que a pesquisa relacional está degradada.

## Justificação

Um esquema tipado impede que relações geradas por linguagem se tornem factos arbitrários. A temporalidade permite responder “era” versus “é” sem destruir história.

## Expansões futuras

Ontologia por domínio, importação de contactos, visualização interativa, raciocínio de regras aprovado e deteção de comunidades.

