# Aurora Platform — RFC 045: Aurora Kernel

**Estado:** Normativo · **Depende de:** RFC 011, 035, 039–040, 043

## Objetivo

Definir o **Aurora Kernel**: o plano de controlo que gere processos cognitivos, estado, permissões, eventos, recursos e execução. O Kernel não pensa nem detém personalidade; gere a Mind e Applications tal como um sistema operativo gere processos e dispositivos.

## Arquitetura

```text
Aurora Platform
├─ Aurora Kernel
│  ├─ Lifecycle Manager        ├─ State/Mind Manager
│  ├─ Event Bus                ├─ Signal Router
│  ├─ Scheduler                ├─ Memory Manager
│  ├─ Permission/Policy Engine ├─ Tool/Capability Manager
│  └─ Resource and Audit Manager
├─ Aurora Mind
│  └─ identidade, memória, conhecimento, crenças, objetivos e cognição
└─ Aurora Applications
   └─ conectores e capacidades autorizadas
```

## Estruturas e interfaces

```text
KernelService
  service_id, role, health, capability_refs[], lifecycle_state

KernelCommand
  id, issuer, command_type, payload_ref, idempotency_key
  policy_refs[], status: ACCEPTED|REJECTED|RUNNING|COMPLETED|FAILED

Kernel.start(instance) -> InstanceLifecycle
Kernel.dispatch(command) -> KernelCommand
Kernel.health() -> KernelHealth
```

## Regras obrigatórias

1. Kernel, Mind e Applications são planos separados com identidades de serviço e permissões próprias.
2. O Kernel é o único proprietário de ciclo de vida, despachos, recursos de execução e barramento.
3. O Kernel não altera semântica da Mind; aplica validação, versionamento, isolamento e entrega de eventos.
4. Falha de uma Application não pode comprometer Kernel ou Mind.

## Casos limite, justificação e expansões

Se o Kernel estiver indisponível, a Mind entra em estado seguro sem novas ações; se a Mind falhar, o Kernel preserva estado/auditoria e recupera-a. Esta separação permite trocar modelo, conector ou interface sem reescrever a plataforma. Expansões: Kernel distribuído, planos de controlo de alta disponibilidade e isolamento por organização.
