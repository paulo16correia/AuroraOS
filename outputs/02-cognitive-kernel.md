# Aurora OS — RFC 02: Núcleo cognitivo

**Estado:** Normativo · **Depende de:** RFC 00–01

## Objetivo

Definir o orquestrador que transforma eventos em respostas, planos ou pedidos de ação. O núcleo coordena componentes; não é uma conversa de modelo nem um local para regras de acesso.

## Responsabilidades

- Normalizar intenção, construir contexto mínimo e iniciar uma unidade de trabalho.
- Decidir entre responder, perguntar, criar objetivo, preparar ação ou recusar.
- Garantir persistência, correlação, idempotência e reflexão posterior.

## Arquitetura e fluxo

```text
Evento autenticado
      │
      ▼
Perceção → triagem de risco → construção de contexto
      │                              │
      ▼                              ▼
interpretação ─────────────→ Thought (proposta)
                                     │
                     ┌───────────────┼───────────────┐
                     ▼               ▼               ▼
                 resposta       planeador       gestor de ferramentas
                     │               │               │
                     └──────→ persistir/reflectir ←──┘
                                      │
                                      ▼
                                saída ao canal
```

## Estruturas de dados

```text
WorkItem
  id, correlation_id, causation_id, event_id, status
  status: RECEIVED|CONTEXTUALIZED|DELIBERATING|WAITING_APPROVAL|
          EXECUTING|COMPLETED|FAILED|CANCELLED
  deadline_at, retry_count, idempotency_key

Thought
  id, work_item_id, intent, requested_outcome
  context_refs[], memory_refs[], graph_refs[], goal_refs[]
  proposed_mode: RESPOND|ASK|PLAN|TOOL_CALL|REFUSE
  proposed_actions[], confidence: 0..1, uncertainty[]
  policy_requirements[], user_explanation, model_trace_ref
  status: DRAFT|VALIDATED|SUPERSEDED|REJECTED

ResponsePlan
  audience, channel, language, content_outline
  citations[], disclose_actions[], needs_user_input: boolean
```

`model_trace_ref` aponta para telemetria protegida; não contém nem expõe raciocínio detalhado ao utilizador. `user_explanation` é uma explicação curta, factual e auditável.

## Interfaces

```text
Kernel.handle(event_id) -> WorkItem
ContextBuilder.build(work_item, budget) -> ContextBundle
Deliberator.propose(context) -> Thought
PolicyEngine.evaluate(thought) -> PolicyDecision[]
Kernel.cancel(work_item_id, actor) -> status
```

`ContextBundle` contém apenas referências autorizadas, resumo, classificação máxima permitida, orçamento de tokens/custo e tempo limite.

## Regras obrigatórias

1. Cada evento DEVE gerar no máximo um `WorkItem` ativo por chave de idempotência.
2. O núcleo DEVE validar todos os campos de `Thought` antes de executar uma proposta.
3. `Thought` NÃO é uma autorização; qualquer ferramenta exige decisão de política independente.
4. O contexto DEVE ser mínimo, temporalmente limitado e rastreável por referências, não por cópias indiscriminadas.
5. A resposta ao utilizador DEVE indicar falha, incerteza relevante ou necessidade de aprovação.

## Casos limite e erro

- **Pedido ambíguo:** produzir `ASK`, sem criar memória factual nem ação.
- **Modelo indisponível:** reencaminhar para modelo alternativo autorizado ou devolver falha transitória; nunca inventar resultado.
- **Evento repetido:** devolver resultado já concluído ou estado atual, sem repetir efeitos.
- **Cancelamento durante ferramenta:** pedir cancelamento ao conector; marcar resultado como `CANCELLED` ou `UNKNOWN` até reconciliação.

## Justificação

O objeto `Thought` cria uma fronteira verificável entre interpretação probabilística e execução determinística. Uma unidade de trabalho persistente permite recuperar de reinícios e impedir duplicação.

## Expansões futuras

Deliberação por múltiplos modelos, simulação de plano, prioridades concorrentes, memória de trabalho multimodal e avaliação contínua de qualidade.

