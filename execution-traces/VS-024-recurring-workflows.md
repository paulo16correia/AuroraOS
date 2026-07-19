# VS-024 — Workflows recorrentes

## Objetivo

Permitir que uma Entity mantenha uma intenção temporal persistente, por exemplo
“Lembra-me todas as segundas às 09:00”, e materialize cada ocorrência uma única
vez através do Scheduler do VS-018.

## Limite

Este slice entrega uma notificação local auditável. Um pedido futuro para Email,
Discord ou outro conector cria uma capability separada e atravessa as Policies
e Approvals normais; uma regra recorrente não é autorização permanente.

## Fluxo

```text
Mensagem do utilizador
       ↓
RecurringWorkflow(ACTIVE, next_due_at)
       ↓
Task(RECURRING_NOTIFICATION, WAITING_FOR_TIME)
       ↓
Scheduler.tick()
       ↓
Task(READY)
       ↓
RecurringRun(DELIVERED) + notificação local
       ↓
Task(COMPLETED) + próxima Task(WAITING_FOR_TIME)
```

## Objetos

`RecurringWorkflow` possui `weekday`, `time_of_day`, `timezone`,
`delivery_channel`, `next_due_at`, `last_triggered_at`, estado e versão.

`RecurringRun` representa uma ocorrência concreta, com a hora prevista, a Task
que a materializou, resultado e instante de entrega.

## Regras obrigatórias

1. A recorrência semanal usa `Europe/Lisbon` e calcula a próxima ocorrência com
   uma biblioteca de timezone, incluindo horário de verão.
2. A identificação de cada Run deriva de `workflow_id + scheduled_for`; repetir
   um tick nunca duplica uma notificação.
3. O Scheduler apenas liberta a Task; a criação do Run conclui a entrega local e
   agenda a ocorrência seguinte.
4. Pausar ou cancelar uma regra impede novas ocorrências, sem apagar Runs
   anteriores.
5. Reiniciar a Entity preserva Workflow, Task pendente e Runs concluídos.

## Casos de erro

- Dia ou hora não reconhecidos: não criar workflow e explicar o formato aceite.
- Tick atrasado: entrega no máximo uma ocorrência vencida e avança para a
  próxima semana; nunca recupera uma avalanche silenciosa.
- Data ambígua na transição de hora: o `scheduled_for` absoluto garante uma
  única ocorrência.

## Critérios de aceitação

- A mensagem de exemplo cria uma regra semanal e uma Task em espera.
- Ao chegar à hora, é criado exatamente um `RecurringRun` e a Task é concluída.
- A Task seguinte é persistida para a semana seguinte.
- Um segundo tick não duplica o Run nem a notificação.
