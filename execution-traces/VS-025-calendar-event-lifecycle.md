# VS-025 - Ciclo de vida de eventos Calendar

## Objetivo

Permitir alterar ou cancelar um evento do Google Calendar de forma
auditável, aprovada pelo utilizador e idempotente. Este slice introduz as
capabilities `CALENDAR_UPDATE_EVENT` e `CALENDAR_DELETE_EVENT`.

## Limite funcional

O Kernel não procura eventos por título, participante ou semelhança. Cada
operação de escrita recebe obrigatoriamente um identificador explícito do
provider: `Evento: <google_event_id>`. Uma alteração também requer `Titulo`,
`Inicio` e `Fim` em ISO 8601. Esta escolha impede que uma mensagem ambígua
altere a reunião errada.

## Fluxo de execução

```text
Mensagem
  |
  v
Plan + Task(INTERNAL_ANALYSIS)
  |
  v
CapabilityRequest(UPDATE_EVENT | DELETE_EVENT)
  |
  v
Policy -> ExecutionPreparation(PENDING_APPROVAL)
  |
  v
ApprovalRequest(PENDING)
  |
  +-- utilizador rejeita --> sem chamada ao provider
  |
  +-- utilizador aprova --> ExecutionRecord(EXECUTING)
                                 |
                                 v
                       Google Calendar API
                                 |
                                 v
                  CapabilityResult(SUCCESS | FAILED)
```

## Contratos

`CalendarEventMutation` representa a intenção validada, nunca o evento
remoto completo. Contém `mutation_id`, `entity_id`, `event_id`, `operation`,
campos propostos de evento, `status`, `validation_error` e `created_at`.

Operações suportadas:

- `CALENDAR_UPDATE_EVENT`: substitui os campos explicitamente fornecidos no
  evento remoto; nesta primeira versão título, início e fim são obrigatórios.
- `CALENDAR_DELETE_EVENT`: remove o evento cujo `event_id` foi fornecido.

## Regras obrigatórias

1. Uma mutação inválida é persistida para auditoria, mas fica `BLOCKED` e não
   cria pedido de aprovação.
2. Toda a escrita Calendar requer Policy permitida e Approval explícita.
3. O `ExecutionRecord` usa a chave idempotente do pedido. Um pedido aprovado
   que já esteja `COMPLETED` devolve o resultado guardado e não chama o
   provider outra vez.
4. Estados `EXECUTING` ou desconhecidos nunca são repetidos automaticamente
   após reinício. Exigem reconciliação com o provider.
5. O provider recebe `sendUpdates=none`; notificações a convidados ficam fora
   deste slice e exigem uma capability própria.
6. Nenhuma correspondência aproximada por título é permitida neste slice.

## Formatos de entrada aceites

```text
Remarca o evento | Evento: abc123 | Titulo: Revisao | Inicio: 2026-07-20T15:00:00+01:00 | Fim: 2026-07-20T16:00:00+01:00

Cancela o evento | Evento: abc123
```

Os nomes de campo sem acentos são recomendados na CLI Windows para evitar
dependência da página de códigos do terminal.

## Casos de erro

- Evento sem identificador: `EVENT_ID_REQUIRED`.
- Alteração sem título, início ou fim: `TITLE_START_END_REQUIRED`.
- Intervalo inválido: `INVALID_EVENT_INTERVAL`.
- Provider não configurado: `GOOGLE_CALENDAR_NOT_CONFIGURED`.
- Falha da API: persistir `FAILED`; não repetir automaticamente.

## Critérios de aceitação

1. Update e delete criam uma `CalendarEventMutation` persistida.
2. Nenhuma chamada Google ocorre antes de `Approval(APPROVED)`.
3. Cada mutação aprovada chama o provider exatamente uma vez.
4. Reinício, replay ou confirmação repetida não duplicam a escrita.
5. O trace inclui `CAPABILITY_REQUEST`, `CALENDAR_EVENT_MUTATION`,
   `APPROVAL_REQUEST`, `EXECUTION` e `CAPABILITY_RESULT`.
