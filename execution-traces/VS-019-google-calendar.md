# VS-019 — Google Calendar

O provider `GoogleCalendarProvider` usa OAuth local, consulta `freeBusy` e cria eventos no calendário `primary`. `credentials.json` e o token OAuth ficam locais e ignorados pelo Git. A criação recebe uma chave de idempotência da Aurora como ID de evento; o executor e a policy continuam a ser as fronteiras de autorização antes de qualquer integração no workflow.
