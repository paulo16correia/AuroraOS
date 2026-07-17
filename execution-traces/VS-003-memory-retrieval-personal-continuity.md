# Aurora OS — Execution Trace Specification: VS-003 Memory Retrieval + Personal Continuity

**Estado:** Autorizado por ADR-002  
**Caso de utilização:** aprender uma preferência declarada → shutdown → restaurar → recuperá-la de forma auditável

## Objetivo

Demonstrar continuidade pessoal entre Sessions. A Aurora deve persistir uma memória episódica estruturada, criada a partir de uma declaração explícita do utilizador, e recuperá-la apenas para a mesma Entity quando a mensagem seguinte for relevante.

## Fluxo normativo

```text
Session 009
  User: "O meu nome é Paulo e gosto de programar Python."
  → Perception → Attention
  → MEMORY_RETRIEVAL([])
  → Context → SelfModel → Thought → Response
  → MemoryCreated(EPISODIC, Paulo, LIKES_PROGRAMMING_LANGUAGE, Python)
  → shutdown

Session 010
  User: "Que linguagem achas que devo usar?"
  → Perception → Attention
  → MEMORY_RETRIEVAL([memory, reason=user_preference, confidence=0.91])
  → Context(memory_refs=[memory]) → SelfModel → Thought → Decision → Response
  → "Como sei que gostas de programar Python, pode ser uma boa escolha dependendo do objetivo."
```

## Contratos mínimos

```text
Memory
  memory_id, entity_id, kind: EPISODIC, status: ACTIVE
  subject: Paulo
  predicate: LIKES_PROGRAMMING_LANGUAGE
  object_value: Python
  confidence: 0.91
  provenance: USER_ASSERTION
  signal_id, observation_id, anchors[], created_at

MemoryRetrieval
  memory_id, reason: user_preference, confidence: 0.91
```

## Regras obrigatórias

1. Só declarações explícitas e reconhecidas pelo extrator determinístico podem criar uma memória VS-003.
2. Recuperação DEVE filtrar primeiro por `entity_id` e `status=ACTIVE`; não existe memória global neste slice.
3. Uma memória recuperada DEVE entrar em `Context.memory_refs` e ser registada em `MEMORY_RETRIEVAL` antes da criação do Context.
4. O provider só pode citar uma preferência se a memória correspondente lhe tiver sido fornecida pelo Kernel.
5. Ausência de resultado produz `MEMORY_RETRIEVAL([])`; nunca justifica uma alegação de lembrança.
6. Memória nova mantém origem, âncoras de Signal/Observation e evento `MemoryCreated`; não é uma frase sem relações.

## Eventos e trace

| Situação | Evento | Trace |
| --- | --- | --- |
| Consulta vazia | `MemoryRetrieved` | `MEMORY_RETRIEVAL(retrieved=[])` |
| Declaração explícita | `MemoryCreated`, `MemoryStored` | `MEMORY(CREATED)` |
| Consulta relevante | `MemoryRetrieved` | `MEMORY_RETRIEVAL(retrieved=[id, reason, confidence])` |
| Preferência usada | `ThoughtCreated` | `MEMORY_RETRIEVAL(USED_FOR_RESPONSE)` |

## Critérios de aceitação

1. A primeira Session cria uma única memória estruturada para Paulo/Python, com proveniência `USER_ASSERTION`.
2. Depois de shutdown e nova Session, a memória é recuperada para a mesma Entity com confiança `0.91`.
3. A resposta recomenda Python com base nessa memória e não afirma conhecimento fora dos dados recuperados.
4. Uma Entity diferente não recupera a memória do Roberto.
5. O trace apresenta a ordem `ATTENTION → MEMORY_RETRIEVAL → CONTEXT → SELF_MODEL`.
6. LAW-008 continua a impedir que a resposta de identidade dependa do provider ou da mensagem.

## Limites deliberados

Não há pesquisa semântica, embeddings, preferências inferidas, consolidação, correção de memória, UI nem acesso a ferramentas. Esses mecanismos serão introduzidos apenas em slices próprios, com critérios de avaliação e permissões explícitas.
