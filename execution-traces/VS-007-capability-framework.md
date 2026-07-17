# Aurora OS — Execution Trace Specification: VS-007 Capability Framework

**Estado:** Autorizado por ADR-006  
**Caso de utilização:** pedido de email → análise interna → capacidade requerida → indisponibilidade explicitamente conhecida

## Objetivo

Permitir à Entity raciocinar sobre capacidades sem executar ferramentas. VS-007 responde “o que me falta para agir?”, não “executa a ação”.

## Fluxo normativo

```text
Entity boot
  → CapabilityRegistry(MEMORY, PLANNING, INTERNAL_REASONING = enabled)
  → CapabilityRegistry(EMAIL_SEND, CALENDAR, FILESYSTEM, WEB_SEARCH = disabled)

User: "Envia um email."
  → Goal → Plan → Task(INTERNAL_ANALYSIS, COMPLETED)
  → CapabilityRequest(EMAIL_SEND, UNAVAILABLE)
  → CAPABILITY_ASSESSMENT(UNAVAILABLE)
  → Thought → Decision(RESPOND)
  → "Não possuo a capacidade EMAIL_SEND neste momento."
  → Observation → Reflection → GoalEvaluation
  → nenhuma Action / ToolCall / provider / rede
```

## Contratos mínimos

```text
Capability
  capability_id: "capability:{entity_id}:{name}"
  entity_id, name, kind
  enabled: bool
  trusted: bool
  version, created_at, updated_at

CapabilityRequest
  request_id: "capability-request:{entity_id}:{task_id}:{name}"
  entity_id, task_ref, capability_ref
  reason
  status: REJECTED|UNAVAILABLE|APPROVED
  created_at
```

## Regras obrigatórias

1. `Capability` descreve disponibilidade; não confere poder de execução.
2. Só o Kernel pode criar `CapabilityRequest`, a partir de uma Task concluída e da Entity proprietária.
3. Em VS-007, requests externas só podem terminar `UNAVAILABLE`; `APPROVED` é reservado para slice futuro e ainda não autoriza execução.
4. `EMAIL_SEND` é definido mas `enabled=false` e `trusted=false`.
5. Nenhum objeto VS-007 pode criar `Action`, `ToolCall`, `CapabilityProvider`, chamada de rede ou segredo.
6. O provider recebe apenas requests validadas pelo Kernel e nunca inventa o estado de capacidade.

## Eventos, trace e erros

| Situação | Evento | Trace | Resultado |
| --- | --- | --- | --- |
| Inicialização | `CapabilityRegistered` | `CAPABILITY_REGISTRY(INITIALIZED)` | Catálogo persistido. |
| Pedido de email | `CapabilityRequestCreated` | `CAPABILITY_REQUEST(CREATED)` | Request persistida. |
| Avaliação | `CapabilityAssessed` | `CAPABILITY_ASSESSMENT(UNAVAILABLE)` | Resposta sem efeito externo. |
| Request duplicada | `CapabilityRequestLoaded` | `CAPABILITY_REQUEST(LOADED)` | Reutilizar; não duplicar. |
| Capability desconhecida | nenhum efeito | erro controlado | Não inferir nem fabricar provider. |

## Critérios de aceitação

1. Uma Entity nova possui as três capacidades internas ativas e `EMAIL_SEND` desativada.
2. “Envia um email por mim” cria Task interna e `CapabilityRequest(EMAIL_SEND, UNAVAILABLE)`.
3. A resposta identifica `EMAIL_SEND` como indisponível.
4. Trace e eventos não contêm Action, ToolCall, provider, rede ou execução de capacidade.
5. Shutdown/restauro preserva o catálogo e a request sem a reavaliar silenciosamente.
6. VS-000–VS-006 permanecem verdes.

## Limites deliberados

Não existem ferramentas, plugins, manifests externos, approval workflow, CapabilityProvider, ToolCall, scheduler, rede ou filesystem. VS-007 é apenas o modelo cognitivo de disponibilidade.
