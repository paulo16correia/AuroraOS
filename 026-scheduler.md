# Aurora OS — RFC 026: Scheduler e vida contínua

**Estado:** Normativo · **Depende de:** RFC 01, 021–022, 040

## Objetivo

Criar eventos temporais e recorrentes de forma fiável. O Scheduler dá ritmo à Aurora, mas não recebe acesso direto a ferramentas nem autorização para agir fora do ciclo cognitivo.

## Arquitetura e fluxo

```text
ScheduleRule → cálculo da próxima ocorrência → JobDue Event
                                            │
                                            ▼
                                  Cognitive Cycle completo
                                            │
                         decisão: agir | pedir aprovação | esperar | notificar
                                            │
                                            ▼
                              registo de execução e próxima ocorrência
```

## Estruturas de dados

```text
Schedule
  id, owner_id, title, trigger: ONCE|CRON|INTERVAL|EVENT_CONDITION
  timezone, expression, next_run_at, last_run_at
  target: CYCLE_TEMPLATE|GOAL|TASK, payload_ref
  enabled, quiet_hours_policy, missed_run_policy
  status: ACTIVE|PAUSED|EXPIRED|FAILED

ScheduleRun
  id, schedule_id, due_at, started_at, finished_at
  status: DUE|STARTED|SKIPPED|SUCCEEDED|FAILED|MISSED
  cycle_id, result_ref, idempotency_key
```

## Interfaces

```text
Scheduler.create(schedule, approval) -> Schedule
Scheduler.tick(now) -> ScheduleRun[]
Scheduler.pause(id, actor) -> Schedule
Scheduler.reconcile(run_id) -> ScheduleRun
```

## Regras obrigatórias

1. Fuso horário é obrigatório; horário de verão e mudanças de relógio DEVEM ser tratados por biblioteca de calendário fiável.
2. Cada ocorrência DEVE ter chave de idempotência e passar pelo ciclo RFC 021.
3. Rotinas que enviam comunicação, mudam dados ou usam conector exigem política/autorização normal; agendamento não é consentimento perpétuo.
4. O utilizador pode pausar, editar ou apagar qualquer schedule; apagar impede futuras ocorrências, não apaga auditoria passada.

## Casos limite e erro

- **Servidor desligado às 08:00:** aplicar `missed_run_policy` (`SKIP`, `RUN_ONCE`, `ASK`); nunca executar uma avalanche por defeito.
- **Hora duplicada no outono:** usar identificador de ocorrência e executar no máximo uma vez salvo regra explícita.
- **Fuso inválido:** desativar schedule e notificar; não assumir UTC silenciosamente.
- **Limite de custo excedido:** marcar `SKIPPED` com razão e pedir alteração da regra.

## Justificação

Separar tempo de execução mantém a Aurora “viva” sem criar um bypass perigoso para ações externas.

## Expansões futuras

Calendários externos, janelas de disponibilidade, prioridades, dependências entre schedules e recuperação distribuída.

