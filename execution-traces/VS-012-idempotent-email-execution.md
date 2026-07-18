# Aurora OS — Execution Trace Specification: VS-012 Idempotent Email Execution

**Estado:** Autorizado por ADR-012  
**Caso de utilização:** rascunho de email válido → aprovação → SMTP → resultado persistente e não repetível

## Objetivo

Executar uma única capability real de ponta a ponta: `EMAIL_SEND`. A execução exige rascunho estruturado, preparação válida e `Approval(APPROVED)`. O resultado é persistido como `SUCCESS`, `FAILED` ou `UNKNOWN`; a recuperação nunca reenvia automaticamente uma operação cuja entrega possa ser ambígua.

## Fluxo normativo

```text
User: "Envia um email para pessoa@dominio | Assunto: ... | Corpo: ..."
  → EmailDraft(VALID)
  → ExecutionPreparation(PENDING_APPROVAL, payload_ref=EmailDraft)
  → ApprovalRequest(PENDING)
  → Approval(APPROVED)
  → ExecutionRecord(EXECUTING) persistido antes da rede
  → SmtpEmailProvider.send(draft, idempotency_key)
       ├─ aceite → ExecutionRecord(COMPLETED) → CapabilityResult(SUCCESS)
       └─ erro → ExecutionRecord(FAILED) → CapabilityResult(FAILED)

Se o processo terminar enquanto `EXECUTING`:
  → restauro → CapabilityResult(UNKNOWN)
  → nenhuma repetição automática; requer reconciliação explícita.
```

## Objetos novos

```text
EmailDraft
  draft_id, entity_id, request_ref
  recipient, subject, body
  status: VALID|INVALID
  validation_error, created_at

ExecutionRecord
  execution_id: execution:{preparation_id}
  entity_id, preparation_ref, approval_ref
  provider_id, idempotency_key
  status: EXECUTING|COMPLETED|FAILED
  external_reference, failure_code
  started_at, completed_at
```

## Provider SMTP

O provider é opt-in. Só pode abrir uma ligação SMTP se as cinco variáveis estiverem presentes no processo:

```text
AURORA_SMTP_HOST
AURORA_SMTP_PORT       (opcional; 587 por defeito)
AURORA_SMTP_USERNAME
AURORA_SMTP_PASSWORD
AURORA_SMTP_FROM
```

O segredo nunca é persistido nem incluído em eventos ou traces. Sem configuração, o resultado é `FAILED(EMAIL_PROVIDER_NOT_CONFIGURED)` sem ligação de rede.

## Regras de idempotência e recuperação

1. `ExecutionRecord(EXECUTING)` é persistido **antes** da chamada ao provider.
2. Cada execução tem uma chave determinística derivada da preparação e é enviada no header `X-Aurora-Idempotency-Key`; o `Message-ID` usa a mesma chave.
3. `COMPLETED` devolve o resultado existente e nunca reenvia.
4. `FAILED` não é repetido automaticamente.
5. `EXECUTING` recuperado transforma-se em resultado `UNKNOWN`; é proibido reenviar sem reconciliação humana ou de provider.
6. Só `Approval(APPROVED)` ligada à mesma preparação pode iniciar execução.
7. Email sem destinatário, assunto ou corpo é `EmailDraft(INVALID)` e bloqueia antes de aprovação.

## Eventos e trace

| Estado | Evento | Trace |
| --- | --- | --- |
| Rascunho | `EmailDraftCreated` | `EMAIL_DRAFT(VALID|INVALID)` |
| Antes da rede | `ExecutionStarted` | `EXECUTION(EXECUTING)` |
| Entrega confirmada | `ExecutionCompleted` | `EXECUTION(COMPLETED)` |
| Falha confirmada | `ExecutionFailed` | `EXECUTION(FAILED)` |
| Recuperação ambígua | `ExecutionUnknown` | `EXECUTION(UNKNOWN)` |

`MindState` passa a conter `execution_record_refs`.

## Critérios de aceitação

1. Um provider controlado recebe um rascunho aprovado e devolve `CapabilityResult(SUCCESS)`.
2. A mesma confirmação não pode invocar o provider uma segunda vez.
3. Sem configuração SMTP, não há rede e o resultado é `FAILED(EMAIL_PROVIDER_NOT_CONFIGURED)`.
4. Falha ou crash em `EXECUTING` nunca desencadeia reenvio automático.
5. Os testes VS-000–VS-011 continuam verdes.

## Limites deliberados

VS-012 implementa apenas SMTP com TLS de início e apenas uma mensagem simples de texto. Não há anexos, HTML, threads, caixas de entrada, retry automático, OAuth, múltiplos providers ou reconciliação assistida. Esses elementos exigem ADRs posteriores.
