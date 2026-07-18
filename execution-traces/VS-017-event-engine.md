# VS-017 — Event Engine

Eventos externos são persistidos como `WorkflowEvent`. Uma task em `WAITING_FOR_EVENT` cujo `wait_ref` corresponda ao tipo do evento transita uma vez para `READY`. Eventos e transições são auditados e sobrevivem a reinícios; tasks já concluídas não são reexecutadas.
