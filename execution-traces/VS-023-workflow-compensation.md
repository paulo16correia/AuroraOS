# VS-023 — Compensação de workflow

## Objetivo

Impedir que um ramo pendente de um workflow composto execute uma ação externa
quando um ramo de que depende semanticamente falha. A compensação desta versão
é exclusivamente preventiva: a Aurora não tenta desfazer ações externas já
concluídas.

## Caso de utilização

```text
Pedido composto
  ├── CALENDAR_CREATE_EVENT ── aprovação ── falha
  └── EMAIL_SEND ───────────── pendente

Falha Calendar
  ↓
WorkflowCompensation(CANCEL_PENDING_BRANCHES)
  ↓
ApprovalRequest do email = CANCELLED
Task WAIT_FOR_APPROVAL do email = CANCELLED
  ↓
Nenhum email é enviado
```

## Contrato

`WorkflowCompensation` é imutável e persistido pelo Kernel:

| Campo | Significado |
| --- | --- |
| `plan_ref` | Plano composto afetado. |
| `failed_request_ref` | CapabilityRequest que falhou ou ficou desconhecido. |
| `blocked_request_refs` | Ramos pendentes impedidos de avançar. |
| `strategy` | `CANCEL_PENDING_BRANCHES` ou `REQUIRES_USER_DECISION`. |
| `status` | `COMPLETED` ou `PENDING_USER_DECISION`. |
| `reason` | Justificação auditável, sem raciocínio interno do modelo. |

## Regras obrigatórias

1. Só o Kernel cria uma compensação.
2. Uma aprovação já concedida nunca é revertida silenciosamente.
3. Não existe rollback externo automático nesta versão.
4. Um ramo apenas é cancelado se a respetiva `ApprovalRequest` ainda estiver
   `PENDING`.
5. A mesma falha produz o mesmo `compensation_id`; repetir o trace apenas
   carrega a decisão persistida.
6. A compensação ocorre antes da resposta ao utilizador e fica visível no trace,
   nos eventos e na persistência.

## Eventos e trace

```text
ExecutionFailed
  ↓
WorkflowBranchCancelled (0..n)
  ↓
WorkflowCompensationCreated
  ↓
trace: COMPENSATION
```

## Casos limite

- Sem ramos pendentes: `REQUIRES_USER_DECISION`; não há rollback.
- Estado `UNKNOWN`: é tratado como falha conservadora e não desbloqueia irmãos.
- Reinício: as aprovações e Tasks canceladas continuam canceladas.
- Repetição da mesma confirmação: não recria nem reexecuta o ramo cancelado.

## Critério de aceitação

Quando Calendar falha e Email está pendente, o trace contém `COMPENSATION`, o
email fica `CANCELLED`, a Task de espera fica `CANCELLED` e o provider SMTP não
é chamado.
