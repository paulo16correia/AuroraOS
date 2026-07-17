# Aurora OS — RFC 021: Ciclo cognitivo obrigatório

**Estado:** Normativo · **Depende de:** RFC 020, 040

## Objetivo

Definir o ciclo completo de uma decisão. É proibido responder, memorizar ou agir por um caminho que omita fases obrigatórias, salvo a resposta estática de saúde documentada.

## Fluxograma normativo

```text
Input/Event
  ↓
Perception → valida origem, normaliza e classifica risco
  ↓
Intent → determina pedido, urgência, ambiguidade e destinatário
  ↓
Attention → seleciona o que merece processamento
  ↓
Working Memory → cria contexto isolado da unidade de trabalho
  ↓
Memory + Knowledge + World Model → recupera evidência autorizada
  ↓
Planner → decompõe se existir objetivo ou ação composta
  ↓
Decision Engine → responde | pergunta | planeia | espera | propõe ferramenta | recusa
  ↓
Policy + Tools → autoriza e executa quando aplicável
  ↓
Observation → interpreta resultado externo
  ↓
Reflection → avalia resultado e propostas de consolidação
  ↓
Learning → aplica apenas mudanças aprovadas
  ↓
Output → resposta, relatório, pedido de confirmação ou silêncio justificado
```

## Estruturas e interfaces

```text
CognitiveCycle
  id, work_item_id, stage, status: RUNNING|WAITING|COMPLETED|FAILED|CANCELLED
  stage_refs{}, started_at, deadline_at, completed_at

CycleStageRecord
  cycle_id, stage, input_refs[], output_refs[], decision_ref
  started_at, ended_at, status, error_ref

Cycle.run(event_id) -> CognitiveCycle
Cycle.advance(cycle_id, stage) -> CycleStageRecord
Cycle.resume(cycle_id, trigger) -> CognitiveCycle
```

## Regras obrigatórias

1. Cada estágio DEVE produzir um registo mínimo ou uma razão de omissão permitida.
2. `Output` só ocorre após `Decision Engine`; `ToolCall` só ocorre depois de política.
3. O ciclo pode terminar em `WAITING` para dados, tempo, aprovação ou disponibilidade externa.
4. A passagem à reflexão é obrigatória após qualquer tarefa ou ferramenta, mesmo que a reflexão conclua “sem aprendizagem”.

## Casos limite e erro

- **Interrupção em qualquer etapa:** persistir estágio atual; retomar ou cancelar de forma idempotente.
- **Input de baixo valor:** Attention pode decidir não iniciar ciclo completo; a decisão e razão são registadas.
- **Prazo excedido:** terminar em `FAILED`/`WAITING`, emitir saída segura e não executar a próxima etapa automaticamente.

## Justificação

O ciclo evita atalhos onde uma frase do modelo vira ação ou memória. Tornar a sequência observável permite testar, depurar e evoluir a arquitetura sem perder governação.

## Expansões futuras

Ciclos paralelos por prioridade, simulação antes da execução, ciclos multimodais e métricas de qualidade por estágio.

