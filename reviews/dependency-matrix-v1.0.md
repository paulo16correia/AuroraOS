# Aurora Platform — Dependency Matrix v1.0

## Central rule

The Kernel only depends on process contracts, state, events, policies and providers. **Does not depend on Mind semantics** — does not interpret memories, beliefs, goals or personality. Mind relies on Kernel contracts for persistence, execution, and events. Applications depend on Kernel/Capability contracts; do not depend on Mind.

| Origin \ Destination | Kernel | Mind | Applications | Infrastructure |
| --- | --- | --- | --- | --- |
| Kernel | internal by interfaces | management contract only | manifests/coordinates | yes, by adapters |
| Mind | yes, by interfaces | internal | never directly | only approved read/persistence services |
| Applications | yes, by SDK/manifest | no | internal | yes, in the authorized sandbox |
| Infrastructure | no | no | no | internal |

## Prohibited dependencies

- Kernel → internal cognitive domain types or Mind prompts/models.
- Application → Mind's canonical storage, someone else's vault or permissions from another Application.
- Mind → connector, network, shell or secret directly.
- Event consumer → producer privileges.

## Minimum nuclear events

| Producer | Event | Typical consumers |
| --- | --- | --- |
| Kernel/Lifecycle | `InstanceStateChanged` | Mind Manager, UI, Audit |
| Mind Manager | `MindChangeApplied` | index, World Model, UI, Audit |
| Decision Engine | `DecisionCommitted` | Capability Resolve, Audit |
| Tool Manager | `ActionObserved` | Reflection, Task Manager, UI |
| Scheduler | `ScheduleDue` | Signal Router |
| Signal Router | `SignalEmitted` | Attention, Situation Assessment |