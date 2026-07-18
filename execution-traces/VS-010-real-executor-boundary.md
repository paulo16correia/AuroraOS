# Aurora OS — Execution Trace Specification: VS-010 Real Executor Boundary

**Estado:** Autorizado por ADR-010  
**Caso de utilização:** pedido de email indisponível → preparação pelo executor de email → bloqueio explícito → nenhum efeito externo

## Objetivo

Substituir o executor inerte de VS-008 por uma fronteira de execução por operação. VS-010 introduz executores concretos que sabem preparar o tipo de trabalho que lhes pertence, mas proíbe a execução externa até existir aprovação do utilizador em VS-011.

## Fluxo normativo

```text
CapabilityRequest(EMAIL_SEND, UNAVAILABLE)
        ↓
ExecutorRegistry.resolve(EMAIL_SEND)
        ↓
EmailSendExecutor.prepare(...)
        ↓
ExecutionPreparation
  operation: EMAIL_SEND
  status: BLOCKED
  block_reason: CAPABILITY_UNAVAILABLE
  approval_required: true
        ↓
CapabilityResult(BLOCKED)
        ↓
Response: a execução foi bloqueada; nenhum email foi enviado
```

## Contratos

```text
CapabilityExecutor.prepare(CapabilityRequest, Capability) -> ExecutionPreparation

ExecutionPreparation
  preparation_id: execution-preparation:{request_id}
  entity_id, request_ref, capability_ref, executor_id
  operation
  status: BLOCKED|PREPARED
  block_reason: str|null
  approval_required: bool
  created_at
```

`ExecutionPreparation` é uma intenção preparada, nunca uma autorização e nunca uma ação. Um `CapabilityResult(BLOCKED|AWAITING_APPROVAL)` declara explicitamente que a execução externa não aconteceu.

## Arquitetura

```text
Kernel
  → ExecutorDispatcher
      → ExecutorRegistry
          ├─ EMAIL_SEND → EmailSendExecutor
          └─ restantes → UnsupportedCapabilityExecutor
      → ExecutionPreparation persistida
      → CapabilityResult persistido

Não existe caminho de Executor para SMTP, HTTP, browser, shell, filesystem ou vault.
```

## Regras obrigatórias

1. O Dispatcher resolve a capability para um executor; o Kernel não conhece detalhes da operação.
2. VS-010 permite exclusivamente `prepare`; não existe método de execução externa invocável pelo ciclo cognitivo.
3. Preparações bloqueadas registam obrigatoriamente uma razão estruturada.
4. Mesmo uma preparação `PREPARED` exige aprovação humana; não produz efeito externo.
5. Preparação e resultado são idempotentes por `CapabilityRequest` e sobrevivem a shutdown/restauro.
6. Cada preparação pertence à mesma Entity da request e da capability.
7. `NOOP_EXECUTOR` é removido; uma capability sem executor usa um fallback bloqueado e auditável.

## Eventos e trace

| Situação | Evento | Trace |
| --- | --- | --- |
| Preparação criada | `ExecutionPrepared` | `EXECUTION_PREPARATION(BLOCKED|PREPARED)` |
| Preparação recarregada | `ExecutionPreparationLoaded` | `EXECUTION_PREPARATION(LOADED)` |
| Segurança bloqueia | `ExecutionBlocked` | `EXECUTION_BLOCKED` |
| Resultado do limite | `CapabilityResultCreated` | `CAPABILITY_RESULT(BLOCKED|AWAITING_APPROVAL)` |

## Casos limite

| Condição | Resultado |
| --- | --- |
| Capability indisponível | `BLOCKED/CAPABILITY_UNAVAILABLE` |
| Capability sem executor | `BLOCKED/NO_EXECUTOR_REGISTERED` |
| Capability de outra Entity | erro de integridade; não persiste preparação |
| Repetição da mesma request | recarrega a preparação e o resultado existentes |
| Entity restaurada | referências de preparação são incluídas no `MindState` |

## Critérios de aceitação

1. Um pedido de email produz `ExecutionPreparation(EMAIL_SEND, BLOCKED)` e `CapabilityResult(BLOCKED)`.
2. O trace contém `EXECUTION_PREPARATION` e `EXECUTION_BLOCKED` antes da resposta.
3. Não existem chamadas de rede, plugins, SMTP, ferramentas, `Action` ou credenciais.
4. A preparação é persistida, auditável e restaurável.
5. Todos os testes VS-000–VS-009 permanecem verdes.

## Limites deliberados

VS-010 não ativa email, não recolhe destinatário/corpo, não cria aprovação, não executa operações nem fornece ferramentas adicionais. VS-011 introduzirá o objeto e o limite de aprovação; VS-012 poderá introduzir uma ferramenta real sob essa fronteira.
