# Aurora OS — RFC 041: World Model — representação da realidade operacional

**Estado:** Normativo · **Depende de:** RFC 04, 040

## Objetivo

Manter uma representação temporal, evidenciada e consultável da realidade relevante para a Aurora. O World Model não guarda frases isoladas: modela entidades, recursos, relações, capacidades e estados observados.

## Arquitetura

```text
Observação / memória confirmada / importação autorizada
                          │
                          ▼
                normalização e resolução de entidade
                          │
                          ▼
            nó + aresta + validade + proveniência
                          │
                          ▼
                World Model versionado
                          │
                          ▼
   atenção, planeamento, decisão e explicação baseada em evidência
```

## Modelo de realidade

```text
Pessoa ─tem→ Conta de email ─recebe→ Mensagem
Pessoa ─participa_em→ Servidor Discord ─contém→ Canal
Pessoa ─trabalha_em→ Projeto ─usa→ VPS ─corre→ Serviço
Serviço ─é_deploy_de→ Repositório ─tem→ Credencial (referência de cofre)
Projeto ─tem→ Objetivo ─decomposto_em→ Tarefa
```

Uma relação afirma apenas o que a sua evidência permite. “A pessoa tem Discord” não implica que a Aurora possa ler esse Discord; acesso é uma relação distinta de permissão.

## Estruturas de dados

```text
WorldAssertion
  id, subject_ref, predicate, object_ref|literal
  evidence_refs[], confidence, valid_from, valid_to
  observed_at, asserted_at, status: PROPOSED|CURRENT|HISTORICAL|DISPUTED|RETRACTED

EntityResolution
  candidate_ref, observed_name, match_score, evidence_refs[]
  decision: MATCH|CREATE|DEFER, decided_by, decided_at

WorldModelVersion
  id, mind_id, parent_version, change_set_refs[], status: DRAFT|ACTIVE|ROLLED_BACK
```

## Interfaces

```text
World.observe(observation_id) -> WorldAssertion[]
World.resolve(entity_candidate) -> EntityResolution
World.query(pattern, as_of, access_context) -> Subgraph
World.reconcile(version_id, evidence) -> WorldModelVersion
```

## Regras obrigatórias

1. Toda a afirmação DEVE separar `observed_at` de `asserted_at` e ser consultável “à data de”.
2. Identidade de pessoa, conta e recurso só é fundida após regra de resolução e evidência suficiente.
3. Permissões, segredos e acesso efetivo são objetos distintos de propriedade e relação social.
4. O World Model DEVE suportar desconhecimento: ausência de aresta não prova ausência no mundo.
5. Uma ferramenta pode criar `Observation`, mas nunca uma asserção `CURRENT` sem validação.

## Casos limite e erro

- **Email ou Discord reassociado:** encerrar validade da relação anterior; não reescrever história.
- **Recurso contraditório:** manter afirmações paralelas `DISPUTED`, não escolher por popularidade do texto.
- **Entidade externa apagada:** marcar observação/estado como inacessível, sem apagar provas históricas permitidas.
- **Importação parcial:** criar versão `DRAFT`; não a tornar fonte para decisões até validação completar.

## Justificação

O modelo do mundo torna possível planear sobre realidade operacional (“qual VPS corre este serviço?”) em vez de procurar frases parecidas. Temporalidade e proveniência impedem que a representação pareça mais certa do que é.

## Expansões futuras

Estado de inventário, gémeos digitais de infraestrutura, simulação de impacto, fontes verificadas e regras declarativas de consistência.

