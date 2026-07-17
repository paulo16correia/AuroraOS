# Aurora Platform — RFC 038: História de vida e narrativa operacional

**Estado:** Normativo · **Depende de:** RFC 020, 036–037, 040, 043

## Objetivo

Manter uma narrativa verificável da história da instância: nascimento, marcos, primeiras capacidades, incidentes, transições de desenvolvimento e aprendizagens relevantes. Não substitui o diário de auditoria nem transforma inferências em autobiografia.

## Arquitetura

```text
eventos auditados + marcos aprovados + DevelopmentState
                              │
                              ▼
                       LifeEpisode candidato
                              │
                              ▼
                 revisão/proveniência → Life History
                              │
                              ▼
            narrativa citada para “quem sou eu / o que mudou”
```

## Estruturas de dados

```text
LifeEpisode
  id, mind_id, kind: BIRTH|MILESTONE|FIRST_USE|INCIDENT|LEARNING|TRANSITION
  occurred_at, title, narrative_summary, evidence_refs[]
  significance: LOW|MEDIUM|HIGH, status: CANDIDATE|VERIFIED|RETRACTED

LifeHistory
  mind_id, episode_refs[], narrative_version, last_compiled_at
```

## Interfaces

```text
LifeHistory.propose(event_refs) -> LifeEpisode
LifeHistory.verify(episode_id) -> LifeEpisode
LifeHistory.narrate(mind_id, audience) -> CitedNarrative
```

## Regras obrigatórias

1. Cada episódio DEVE referenciar provas auditáveis e data/intervalo.
2. A narrativa distingue acontecimentos confirmados de resumos interpretativos.
3. O utilizador pode corrigir texto narrativo, sem alterar o diário de auditoria subjacente.
4. Dados sensíveis não entram numa narrativa sem política de divulgação aplicável.

## Casos limite e erro

- **“Primeiro erro” não identificável:** responder que não existe evidência suficiente, não escolher um episódio arbitrário.
- **Episódio retractado:** removê-lo da narrativa atual, preservando rasto de revisão protegido.

## Justificação

História de vida fornece continuidade compreensível sem tratar uma coleção de memórias como identidade narrativa automática.

## Expansões futuras

Linha temporal visual, narrativas por projeto, marcos partilhados e resumos de manutenção anuais.

