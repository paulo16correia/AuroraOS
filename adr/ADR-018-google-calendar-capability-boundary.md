# ADR-018 — Calendar Create Event como capability aprovada

## Contexto

O provider Google Calendar já autentica através de OAuth local, mas um provider não deve ser invocado diretamente pelo Kernel nem por uma proposta de LLM.

## Decisão

Introduzir `CALENDAR_CREATE_EVENT` como capability externa própria. A criação exige uma policy ativa, um `CalendarEventDraft` válido e uma `ApprovalRequest` persistida. O `CalendarCreateEventExecutor` é o único adaptador que pode chamar `GoogleCalendarProvider.create_event`.

## Consequências

O fluxo é auditável, recuperável e idempotente. A primeira interface de linguagem exige valores ISO 8601 explícitos, preservando a fronteira entre interpretação de linguagem natural e efeitos no calendário. A consulta `freeBusy` será uma capability de leitura futura, com policy própria.
