# VS-018 — Scheduler persistente

## Objetivo

Retomar uma `Task` em `WAITING_FOR_TIME` quando a hora persistida chega, sem
reexecutar uma transição já efetuada nem chamar capabilities diretamente.

```text
Task(WAITING_FOR_TIME, wait_until)
             ↓
PersistentScheduler.tick(now)
             ↓
Task(READY)
             ↓
TASK_RESUMED + trace SCHEDULER
```

## Regras obrigatórias

- `wait_until` é ISO-8601 com offset.
- Só Tasks vencidas em `WAITING_FOR_TIME` transitam para `READY`.
- O segundo tick observa `READY` e não produz nova transição: é idempotente.
- O Scheduler não executa capabilities nem concede aprovação implícita.
- A Task é persistida antes do evento e do trace.

## Critério de aceitação

Uma Task vencida transita uma vez para `READY`, emite `TaskResumed` com razão
`DUE_TIME`, sobrevive a reinício e não volta a ser retomada num tick seguinte.
