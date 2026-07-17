# Aurora Platform — RFC 052: Missões, objetivos e tarefas

**Estado:** Normativo · **Depende de:** RFC 05, 035, 040, 051

## Objetivo

Introduzir **Missão** como nível de intenção acima de Goal. Uma missão descreve um propósito duradouro e os limites de sucesso; Goals convertem esse propósito em resultados; Plans e Tasks realizam trabalho verificável.

## Hierarquia

```text
Constituição / Genome
          │ limita
Mission: “manter o utilizador organizado”
          │ orienta
Goal: “organizar os emails desta semana”
          │ decomposto em
Plan → Task: “criar rascunho de classificação” → Action/ToolCall
```

## Estruturas de dados

```text
Mission
  id, mind_id, title, purpose, success_definition, boundaries[]
  priority_policy, owner, status: DRAFT|ACTIVE|PAUSED|RETIRED
  goal_refs[], review_at, evidence_refs[]
```

## Interfaces

```text
Missions.create(definition, approval) -> Mission
Missions.align(goal_id, mission_id) -> Goal
Missions.review(mission_id) -> ReviewReport
```

## Regras obrigatórias

1. Missões não são ordens de execução e não substituem aprovação de Goals/Tasks de risco.
2. Todo Goal persistente deve estar associado a uma missão ou ser marcado `AD_HOC` com prazo de revisão.
3. A missão não pode justificar recolha de dados, comunicação ou autonomia fora da Constituição/Leis/políticas.
4. Missões são revistas, pausadas e retiradas pelo proprietário; não evoluem por inferência automática.

## Casos limite e erro

- **Missões em conflito:** Decision Engine usa prioridades/limites definidos e pede orientação se não houver resolução segura.
- **Goal sem missão:** permitido apenas como `AD_HOC`, com dono e expiração.

## Justificação

Missões evitam que tarefas isoladas pareçam estratégia. Separar propósito de execução mantém a Aurora coerente ao longo de semanas ou meses.

## Expansões futuras

Missões partilhadas de equipa, indicadores de missão e revisão estratégica periódica.

