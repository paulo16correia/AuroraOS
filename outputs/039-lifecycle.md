# Aurora Platform — RFC 039: Ciclo de vida da instância

**Estado:** Normativo · **Depende de:** RFC 011, 026–027, 033, 036, 043

## Objetivo

Especificar os estados macro de existência de uma instância Aurora e as transições permitidas entre boot, disponibilidade, deliberação, execução, manutenção, cópia de segurança e encerramento.

## Máquina de estados

```text
CREATED → BOOTSTRAPPING → RECOVERING → READY
                                     │
                  ┌──────────────────┼──────────────────┐
                  ▼                  ▼                  ▼
              DELIBERATING       EXECUTING        MAINTAINING
                  │                  │                  │
                  └──────────────→ WAITING ←─────────────┘
                                     │
                                     ▼
                          PAUSED | BACKING_UP | UPDATING
                                     │
                                     ▼
                              SHUTTING_DOWN → STOPPED → RETIRED
```

## Estruturas e interfaces

```text
InstanceLifecycle
  instance_id, state, entered_at, reason, active_cycle_refs[]
  pending_action_refs[], last_verified_snapshot_ref, version

Lifecycle.transition(instance_id, target_state, reason) -> InstanceLifecycle
Lifecycle.prepareShutdown(instance_id) -> ShutdownPlan
Lifecycle.resume(instance_id) -> InstanceLifecycle
```

## Regras obrigatórias

1. Não pode entrar em `STOPPED` sem snapshot verificado ou razão de emergência auditada.
2. `UPDATING` e `BACKING_UP` drenam trabalho idempotente e marcam chamadas externas incompletas para reconciliação.
3. `PAUSED` impede novos efeitos; `WAITING` preserva ciclos dependentes de dados, tempo ou aprovação.
4. Apenas o Kernel pode executar transições; a Mind pode propor, nunca alterar o ciclo de vida diretamente.

## Casos limite, justificação e expansões

Falha abrupta é observada como `RECOVERING`, não como `READY`; o Kernel reconcilia antes de reativar. O ciclo de vida torna a existência operacional da Aurora controlável, recuperável e observável. Futuramente suporta hibernação, migração a quente e múltiplas instâncias.
