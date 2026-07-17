# Aurora OS — RFC 022: Motor de decisão

**Estado:** Normativo · **Depende de:** RFC 01, 021, 040

## Objetivo

Separar a decisão de agir da geração de uma resposta. O Motor de Decisão escolhe um modo de ação a partir de intenção, evidência, risco, estado da Mind, políticas e contexto ativo.

## Arquitetura

```text
Thought + Context + Attention + políticas + estado de objetivos
                           │
                           ▼
                   avaliação de opções
      ┌───────┬─────────┬──────────┬─────────┬──────────┐
      ▼       ▼         ▼          ▼         ▼          ▼
 responder perguntar planear esperar ferramenta recusar/silêncio
                           │
                           ▼
                   DecisionRecord → ciclo cognitivo
```

## Estruturas de dados

```text
Decision
  id, cycle_id, mode: RESPOND|ASK|CREATE_GOAL|PLAN|WAIT|TOOL_CALL|REFUSE|SILENT
  objective_ref, selected_option, alternatives_considered[]
  evidence_refs[], uncertainty[], risk_level, confidence
  policy_decision_ids[], approval_required, expiry_at
  status: PROPOSED|COMMITTED|EXECUTED|SUPERSEDED|EXPIRED

DecisionOption
  mode, rationale_summary, expected_effects[], cost_estimate
  risk, prerequisites[], blocking_reasons[]
```

## Interfaces

```text
DecisionEngine.evaluate(thought, context) -> Decision
DecisionEngine.commit(decision_id, policy_results) -> Decision
DecisionEngine.invalidate(decision_id, reason) -> Decision
```

## Regras obrigatórias

1. O motor DEVE avaliar pelo menos: relevância, evidência, risco, custo, permissões e reversibilidade.
2. `TOOL_CALL` sem política `ALLOW` ou aprovação válida não pode ser `COMMITTED`.
3. `SILENT` só é permitido por regra explícita de canal, privacidade, limite de ruído ou ausência de destinatário; nunca para ocultar falha.
4. Decisões expiram quando contexto, aprovação, dados ou prazo deixam de ser válidos.

## Casos limite e erro

- **Confiança elevada sem fonte:** reduzir para `ASK` ou `RESPOND` com incerteza; nunca aumentar privilégio.
- **Duas opções equivalentes:** preferir menor efeito externo e menor custo, ou pedir preferência.
- **Nova informação invalida decisão:** marcar `SUPERSEDED` antes de iniciar efeito.
- **Motor indisponível:** bloquear ferramentas; só permitir respostas estáticas aprovadas.

## Justificação

Responder é uma escolha com efeitos sociais e operacionais. Um módulo explícito permite testar decisões e evita que a linguagem do modelo determine a política de ação.

## Expansões futuras

Utilidade configurável, simulação de impacto, decisões colegiais e políticas de atenção por objetivo.

