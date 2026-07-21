# Aurora OS — RFC 03: Memória persistente

**Estado:** Normativo · **Depende de:** RFC 01–02

## Objetivo

Especificar memória útil, corrigível e com proveniência. Memória é um conjunto de registos de domínio, não um arquivo ilimitado de conversas.

## Arquitetura

```text
Eventos/documentos → extração candidata → validação de política
                                              │
                      ┌───────────────────────┼──────────────┐
                      ▼                       ▼              ▼
                 episódica               semântica      procedimental
                      └─────────→ índice + proveniência ←─────┘
                                             │
Consulta → filtro ACL/classificação → pesquisa híbrida → contexto
```

## Estruturas de dados

```text
Memory
  id, kind: EPISODIC|SEMANTIC|PROCEDURAL|WORKING
  subject_ref, predicate, object_json, summary
  source_refs[], evidence_refs[], confidence: 0..1
  status: CANDIDATE|ACTIVE|DISPUTED|SUPERSEDED|RETRACTED|EXPIRED
  sensitivity: PUBLIC|PRIVATE|CONFIDENTIAL|SECRET
  access_policy_id, valid_from, valid_to, retention_until
  embedding_ref, created_by: USER|SYSTEM|IMPORT

MemoryRevision
  id, memory_id, operation: CREATE|CONFIRM|CORRECT|MERGE|RETRACT|EXPIRE
  actor, reason, prior_hash, new_hash, at
```

`WORKING` é efémera e não entra na pesquisa duradoura. Factos inferidos sem confirmação iniciam em `CANDIDATE`; só podem orientar perguntas ou sugestões, não ações de alto impacto.

## Interfaces

```text
MemoryService.record(candidate, provenance) -> Memory
MemoryService.search(query, access_context, filters) -> RankedMemory[]
MemoryService.revise(memory_id, operation, actor) -> MemoryRevision
MemoryService.forget(memory_id, actor) -> tombstone
```

## Regras obrigatórias

1. Uma memória DEVE ter origem e política de acesso; sem ambas, não é persistida.
2. Pesquisa DEVE aplicar ACL e classificação antes de cálculo semântico.
3. Correção pelo proprietário DEVE prevalecer sobre inferência automática.
4. `RETRACTED` não é apagada imediatamente do registo de auditoria, mas deixa de ser recuperável para raciocínio normal.
5. O sistema NÃO DEVE consolidar segredos, credenciais ou dados sensíveis de terceiros sem regra específica.

## Casos limite e erro

- **Memórias contraditórias:** manter ambas, marcar conflito e pedir confirmação quando material.
- **Eliminação solicitada:** remover de índices ativos, propagar a caches e marcar retenção legal aplicável; informar âmbito real.
- **Pesquisa sem evidência:** responder que não encontrou confirmação, nunca preencher lacunas com uma memória semelhante.
- **Falha de índice:** recorrer a consulta estruturada limitada; não declarar ausência de memória com certeza.

## Justificação

Separar candidatos de factos ativos reduz alucinações persistentes. Proveniência e revisões permitem corrigir uma memória sem apagar a história de como o sistema chegou a uma conclusão.

## Expansões futuras

Consolidação agendada, importação documental com consentimento, retenção por jurisdição, memórias partilhadas por equipa e avaliação automática de obsolescência.

