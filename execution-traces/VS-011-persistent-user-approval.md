# Aurora OS — Execution Trace Specification: VS-011 Persistent User Approval

**Estado:** Autorizado por ADR-011  
**Caso de utilização:** pedido de email → preparação pendente → confirmação persistente → nenhuma execução externa

## Objetivo

Introduzir autorização explícita, persistente e atribuída a um utilizador. Uma mensagem de confirmação deixa de ser contexto transitório: torna-se uma `Approval` ligada a uma `ApprovalRequest`, que por sua vez referencia uma `ExecutionPreparation` concreta.

## Fluxo normativo

```text
CapabilityRequest(EMAIL_SEND, READY_FOR_APPROVAL)
        ↓
EmailSendExecutor.prepare
        ↓
ExecutionPreparation(PENDING_APPROVAL)
        ↓
ApprovalRequest(PENDING)
        ↓
Response: "Posso preparar este envio. Queres confirmar?"

--- reinício permitido; não prepara nem executa novamente ---

User: "Sim"
        ↓
Approval(APPROVED, actor_id)
        ↓
ApprovalRequest(APPROVED)
        ↓
Response: aprovação registada; execução externa ainda desativada
```

VS-011 não chama `Executor.execute`, não usa SMTP e não pode produzir `CapabilityResult(SUCCESS)` para uma ação externa.

## Modelo de domínio

```text
ExecutionPreparation 1 ─── 1 ApprovalRequest 0 ─── 1 Approval

ApprovalRequest.status: PENDING | APPROVED | REJECTED | EXPIRED
Approval.decision:       APPROVED | REJECTED
```

```text
ApprovalRequest
  approval_request_id: approval-request:{preparation_id}
  entity_id
  preparation_ref
  capability_ref
  operation_summary
  status, expires_at, version, created_at, updated_at

Approval
  approval_id: approval:{approval_request_id}
  entity_id
  approval_request_ref
  decision
  actor_id
  decided_at
```

## Regras obrigatórias

1. Só uma `ExecutionPreparation(PENDING_APPROVAL)` pode originar uma `ApprovalRequest`.
2. A confirmação só é aceite quando existe exatamente uma aprovação pendente para a Entity ativa.
3. A decisão regista `actor_id`, instante e a request exata; não é reutilizável por outra Entity, preparação ou capability.
4. Uma `Approval(APPROVED)` não é uma execução nem concede capacidades novas.
5. Repetir o pedido, restaurar a Entity ou repetir a confirmação não pode recriar a preparação nem a aprovação.
6. `REJECTED` e `EXPIRED` encerram a autorização; VS-011 não reabre automaticamente o pedido.
7. A LLM pode descrever o estado, mas o Kernel é o único componente que interpreta a confirmação e persiste a aprovação.

## Eventos e trace

| Situação | Evento | Trace |
| --- | --- | --- |
| Pedido criado | `ApprovalRequestCreated` | `APPROVAL_REQUEST(PENDING)` |
| Pedido restaurado | `ApprovalRequestLoaded` | `APPROVAL_REQUEST(LOADED)` |
| Decisão persistida | `ApprovalRecorded` | `APPROVAL(APPROVED|REJECTED)` |

`MindState` inclui `approval_request_refs` e `approval_refs` no shutdown.

## Casos limite

| Condição | Resultado obrigatório |
| --- | --- |
| mensagem "Sim" sem pedido pendente | não cria Approval; resposta explica a ausência |
| mais de um pedido pendente | confirmação textual é recusada por ambiguidade |
| restart antes de confirmar | recarrega o pedido PENDING; não prepara nem executa de novo |
| confirmação repetida | usa o Approval já persistido; não duplica decisão |
| pedido rejeitado | não invoca executor externo |

## Critérios de aceitação

1. EMAIL_SEND gera `ExecutionPreparation(PENDING_APPROVAL)` e `ApprovalRequest(PENDING)`.
2. Após restart, a preparação, o resultado e o pedido de aprovação são carregados sem duplicação.
3. `Sim` cria `Approval(APPROVED)` e atualiza a request para `APPROVED`.
4. Não existe rede, SMTP, segredo, Action, ToolCall ou execução de capability.
5. Todos os testes VS-000–VS-010 permanecem verdes.

## Limites deliberados

Esta versão não recolhe ainda destinatário, assunto ou corpo de email, nem implementa expiração temporal ativa ou UI de aprovação. VS-012 adicionará o executor de email real apenas quando a request tiver preparação completa e `Approval(APPROVED)` válida.
