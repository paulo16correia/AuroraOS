# VS-020 — Calendar Free/Busy

## Objetivo

Introduzir `CALENDAR_FREEBUSY` como capability externa de leitura. A intenção é responder a consultas de disponibilidade sem transformar uma leitura num efeito externo de escrita.

```text
Mensagem do utilizador
        ↓
CapabilityRequest(CALENDAR_FREEBUSY, READY)
        ↓
Policy(NOT_REQUIRED)
        ↓
CalendarAvailabilityQuery
        ↓
ExecutionPreparation(READY)
        ↓
CalendarFreeBusyExecutor
        ↓
GoogleCalendarProvider.freebusy
        ↓
ExecutionRecord(COMPLETED)
        ↓
CapabilityResult(SUCCESS)
```

## Regras

- A mensagem que pede disponibilidade autoriza apenas a consulta de leitura atual.
- `CALENDAR_FREEBUSY` nunca cria, altera ou elimina eventos.
- A policy é persistida e pode ser alterada para exigir aprovação, se o proprietário da entidade assim o decidir.
- A execução tem `approval_ref = approval:not-required`, tornando a ausência de aprovação explícita auditável, e não implícita.
- O resultado e a `CalendarAvailabilityQuery` são persistidos; repetir a mesma mensagem com a mesma chave de idempotência não volta a consultar a API.

## Intenções suportadas

```text
Estou livre amanhã às 15h?
Quando tenho uma hora livre esta semana?
```

A primeira consulta avalia uma janela de uma hora. A segunda procura a primeira janela livre de 60 minutos, em dias úteis, entre as 09:00 e as 18:00 locais, num horizonte de sete dias.

## Limites conhecidos

O VS-020 consulta apenas o calendário `primary`. Resolução de nomes, múltiplos calendários, preferências de horário e fuso horário por entidade pertencem a slices posteriores.
