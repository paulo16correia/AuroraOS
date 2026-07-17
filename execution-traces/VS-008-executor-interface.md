# Aurora OS — Execution Trace Specification: VS-008 Executor Interface

**Estado:** Autorizado por ADR-008  
**Caso de utilização:** pedido externo indisponível → executor de referência → resultado persistente sem efeito

## Objetivo

Definir a ponte contratual para execução sem executar capacidades reais. VS-008 prova que o Kernel consegue despachar uma request para um componente separado e receber um resultado que pode ser auditado.

## Fluxo normativo

```text
User: "Envia um email por mim"
  → Task(INTERNAL_ANALYSIS, COMPLETED)
  → CapabilityRequest(EMAIL_SEND, UNAVAILABLE)
  → Executor(NoopExecutor)
  → CapabilityResult(NOT_IMPLEMENTED)
  → Thought / Decision(RESPOND)
  → Response: nenhum email foi enviado
  → Observation / Reflection

Nenhuma Action, ToolCall, provider, rede, segredo ou chamada externa.
```

## Contratos mínimos

```text
Executor.execute(request, capability) -> CapabilityResult

CapabilityResult
  result_id: "capability-result:{request_id}"
  entity_id
  request_ref: CapabilityRequest
  executor_id: NOOP_EXECUTOR
  status: DRY_RUN|NOT_IMPLEMENTED
  summary
  created_at
```

## Regras obrigatórias

1. Um Executor recebe apenas uma `CapabilityRequest` persistida e a Capability referida pela mesma Entity.
2. VS-008 só permite `NOOP_EXECUTOR`; qualquer executor externo falha na composição do Kernel.
3. `NOT_IMPLEMENTED` e `DRY_RUN` nunca são prova de efeito externo, sucesso, envio ou autorização.
4. A request não muda de estado quando o executor devolve resultado.
5. Cada resultado DEVE referenciar a request, o executor e o trace que o originou.
6. O resultado é idempotente por `request_ref`; repetir o ciclo carrega-o, não recria nem reexecuta.

## Eventos e trace

| Situação | Evento | Trace |
| --- | --- | --- |
| Despacho interno | `ExecutorInvoked` | `EXECUTOR(NOT_IMPLEMENTED)` |
| Resultado criado | `CapabilityResultCreated` | `CAPABILITY_RESULT(CREATED)` |
| Resultado existente | `CapabilityResultLoaded` | `CAPABILITY_RESULT(LOADED)` |

## Critérios de aceitação

1. Um pedido de email cria/recupera `CapabilityRequest(UNAVAILABLE)` e um `CapabilityResult(NOT_IMPLEMENTED)`.
2. O resultado sobrevive a shutdown/restauro e não é duplicado.
3. Resposta e trace distinguem `UNAVAILABLE` de `NOT_IMPLEMENTED`.
4. Não existe Action, ToolCall, provider, plugin, rede, ficheiro ou segredo.
5. VS-000–VS-007.1 permanecem verdes.

## Limites deliberados

Não existe LLM Adapter, executor de email, approval workflow, plugin SDK, scheduler, queue externa, retry ou autonomia temporal. VS-008 define somente o contrato de passagem de intenção para resultado inerte.
