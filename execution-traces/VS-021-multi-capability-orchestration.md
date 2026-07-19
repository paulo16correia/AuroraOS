# VS-021 — Multi-capability orchestration

## Objetivo

Demonstrar que um único Goal pode materializar vários ramos de capability sem acoplar executores entre si.

```text
Pedido composto
       ↓
INTERNAL_ANALYSIS
       ↓
PLAN
  ┌────┴──────────────────────────┐
  ↓                               ↓
CALENDAR_CREATE_EVENT         EMAIL_SEND
  ↓                               ↓
ApprovalRequest(Calendar)    ApprovalRequest(Email)
  ↓                               ↓
Executor Calendar             Executor Email
  └───────────────┬───────────────┘
                  ↓
            Final Response
```

## Regras obrigatórias

- Cada branch cria a sua própria `CapabilityRequest`, `ExecutionPreparation`, `ApprovalRequest`, `ExecutionRecord` e `CapabilityResult`.
- Uma aprovação de Calendar nunca pode autorizar um Email, e vice-versa.
- Quando existem vários pedidos pendentes, `Sim` não escolhe arbitrariamente. O utilizador indica `Aprovo calendário`, `Aprovo email` ou `Aprovo tudo`.
- A conclusão ou falha de uma branch não reexecuta a outra.
- A ordem de criação das branches é estável: Calendar e, depois, Email. Isto preserva replay determinístico.

## Contrato de entrada inicial

```text
Marca uma reunião | Título: Revisão | Início: 2026-07-20T15:00:00+01:00 | Fim: 2026-07-20T16:00:00+01:00 | Envia um email para joao@example.com | Assunto: Reunião | Corpo: Confirmas?
```

O destinatário de Email é explícito. Resolver “João” para um contacto pertence ao VS-022 e não é inferido neste slice.
