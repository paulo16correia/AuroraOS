# Aurora Platform — Matriz de dependências v1.0

## Regra central

O Kernel depende apenas de contratos de processo, estado, eventos, políticas e providers. **Não depende de semântica da Mind** — não interpreta memórias, crenças, objetivos ou personalidade. A Mind depende dos contratos do Kernel para persistência, execução e eventos. Applications dependem de contratos de Kernel/Capability; não dependem da Mind.

| Origem \ Destino | Kernel | Mind | Applications | Infraestrutura |
| --- | --- | --- | --- | --- |
| Kernel | interno por interfaces | apenas contrato de gestão | manifesta/coordena | sim, por adaptadores |
| Mind | sim, por interfaces | interno | nunca diretamente | apenas serviços de leitura/persistência aprovados |
| Applications | sim, por SDK/manifest | não | interno | sim, no sandbox autorizado |
| Infraestrutura | não | não | não | interno |

## Dependências proibidas

- Kernel → tipos de domínio cognitivo internos ou prompts/modelos da Mind.
- Application → armazenamento canónico da Mind, cofre alheio ou permissões de outra Application.
- Mind → conector, rede, shell ou segredo diretamente.
- Consumer de evento → privilégios do produtor.

## Eventos nucleares mínimos

| Produtor | Evento | Consumidores típicos |
| --- | --- | --- |
| Kernel/Lifecycle | `InstanceStateChanged` | Mind Manager, UI, Auditoria |
| Mind Manager | `MindChangeApplied` | índice, World Model, UI, Auditoria |
| Decision Engine | `DecisionCommitted` | Capability Resolver, Auditoria |
| Tool Manager | `ActionObserved` | Reflection, Task Manager, UI |
| Scheduler | `ScheduleDue` | Signal Router |
| Signal Router | `SignalEmitted` | Attention, Situation Assessment |

