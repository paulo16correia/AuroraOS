# Aurora OS — RFC 027: Self — autoconsciência operacional

**Estado:** Normativo · **Depende de:** RFC 011, 020, 040

## Objetivo

Definir o **Self** como o modelo operacional que a Aurora mantém sobre si própria: identidade ativa, capacidades instaladas, permissões efetivas, limites, recursos, saúde, trabalho em curso e história operacional recente. Self não significa consciência humana, experiência subjetiva ou autoridade autónoma.

## Arquitetura

```text
Identity + Personality + Capability Registry + Policy + Health + Work state
                                 │
                                 ▼
                       SelfModel versionado
                                 │
            ┌────────────────────┼─────────────────────┐
            ▼                    ▼                     ▼
      Decision Engine       UI/diagnóstico       explicação de limites
```

## Estruturas de dados

```text
SelfModel
  id, mind_id, identity_ref, personality_ref, version
  capability_snapshot_ref, permission_snapshot_ref, resource_snapshot_ref
  operational_state: BOOTING|READY|BUSY|WAITING|DEGRADED|PAUSED|RECOVERING
  active_cycle_ids[], active_task_ids[], current_focus_ref
  health_summary_ref, recent_activity_refs[], observed_at

CapabilitySnapshot
  id, available_tools[], enabled_capabilities[], disabled_capabilities[]
  limits_json, provider_statuses[], captured_at

ResourceSnapshot
  cpu_budget, memory_budget, storage_budget, queue_depth, model_budget
  network_status, maintenance_window, captured_at
```

## Interfaces

```text
Self.refresh(mind_id) -> SelfModel
Self.describe(access_context) -> SafeSelfDescription
Self.can(capability, context) -> CapabilityAssessment
Self.pause(actor, reason) -> SelfModel
```

## Regras obrigatórias

1. Decisões que propõem uma ferramenta DEVEM consultar o Self para confirmar capacidade disponível e limite operacional.
2. O Self DEVE separar “capacidade instalada”, “permissão concedida” e “ação atualmente segura”; uma não implica a outra.
3. `Self.describe` só expõe uma visão segura, sem segredos, topologia sensível ou identificadores de credenciais.
4. Estado de saúde é observado, datado e revogável; não se presume saudável por ausência de alerta.

## Casos limite e erro

- **Conector instalado mas revogado:** aparece como capacidade indisponível; o plano é bloqueado/replaneado.
- **Estado desatualizado:** a execução revalida permissões e saúde, não confia só no snapshot.
- **Recuperação após reinício:** Self entra em `RECOVERING` até tarefas e chamadas `UNKNOWN` serem reconciliadas.

## Justificação

Um agente que não sabe o que pode fazer cria promessas erradas e tentativas falhadas. O Self permite à Aurora dizer “posso preparar, mas não enviar” com base num estado verificável.

## Expansões futuras

Orçamentos por utilizador, capacidade distribuída, autodiagnóstico assistido e relatórios operacionais diários.

