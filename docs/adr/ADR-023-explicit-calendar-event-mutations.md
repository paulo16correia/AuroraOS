# ADR-023 - Mutações Calendar requerem identificador explícito

**Estado:** Aceite  
**Data:** 2026-07-19

## Contexto

Alterar ou eliminar um evento por correspondência textual é perigoso: podem
existir várias reuniões com o mesmo título, hora semelhante ou participantes
iguais. A interpretação de linguagem natural não é uma autoridade suficiente
para escolher um objeto remoto destrutivo.

## Decisão

O VS-025 exige `Evento: <provider_event_id>` em todas as mutações Calendar.
Para update, a primeira versão também exige título, início e fim completos.
O Kernel persiste esta intenção como `CalendarEventMutation`, prepara-a,
obtém Approval e delega apenas a chamada aprovada ao provider.

## Consequências

- Aumenta a segurança e a auditabilidade de operações destrutivas.
- Evita pesquisas aproximadas e alterações acidentais.
- Obriga a UI futura a listar eventos e apresentar os seus identificadores.
- Mantém aberta uma evolução futura para `EVENT_RESOLUTION`, desde que essa
  capability produza uma escolha explícita confirmada pelo utilizador.

## Alternativas rejeitadas

- **Escolher o primeiro evento com título semelhante:** não determinístico e
  inseguro.
- **Delegar a escolha ao LLM:** o modelo não é fonte de verdade do estado
  remoto.
- **Permitir update parcial silencioso:** torna difícil explicar o estado
  final e aumenta o risco de remover dados de evento.
