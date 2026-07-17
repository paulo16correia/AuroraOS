# Aurora OS — RFC 043: Mind State — serialização, recuperação e continuidade

**Estado:** Normativo · **Depende de:** RFC 020, 024, 027–029, 040–042

## Objetivo

Definir o estado serializável da Aurora num instante, para que uma interrupção, migração ou reinício retome o trabalho de forma segura e verificável. Um Mind State não é um despejo de base de dados: é um snapshot coerente de agregados e referências, com consistência temporal.

## Arquitetura

```text
Mind + Self + World Model + ciclos/tarefas + scheduler + ferramentas
                                │
                                ▼
                      barreira de consistência
                                │
                                ▼
              MindStateSnapshot cifrado, versionado e assinado
                                │
             ┌──────────────────┴──────────────────┐
             ▼                                     ▼
       reinício/migração                     auditoria/exportação autorizada
             │                                     │
             ▼                                     ▼
      RECOVERING → reconciliação → ACTIVE     visão redigida por ACL
```

## Estruturas de dados

```text
MindStateSnapshot
  id, mind_id, schema_version, captured_at, consistency_cursor
  identity_ref, personality_ref, self_ref, belief_refs[], preference_refs[]
  relationship_refs[], goal_refs[], active_task_refs[], plan_refs[]
  attention_state_ref, working_memory_refs[], world_model_version
  tool_state_refs[], scheduler_state_refs[], interaction_state_ref
  policy_set_version, health_ref, audit_anchor_hash, encryption_metadata
  status: CREATING|COMPLETE|VERIFIED|RESTORED|EXPIRED|CORRUPT

RecoveryPlan
  id, snapshot_id, target_environment, steps[]
  unresolved_tool_call_refs[], reconciliation_policy, status
  status: PLANNED|RUNNING|WAITING_RECONCILIATION|COMPLETED|FAILED
```

`tool_state_refs` contêm apenas identificadores e estados, nunca segredos. `working_memory_refs` são incluídas apenas se a política de curta retenção permitir; itens abertos podem ser selados, descartados ou convertidos em pedidos de retoma.

## Interfaces

```text
MindState.capture(mind_id, consistency_level) -> MindStateSnapshot
MindState.verify(snapshot_id) -> VerificationReport
MindState.restore(snapshot_id, environment) -> RecoveryPlan
MindState.export(snapshot_id, access_context) -> RedactedExport
```

## Regras obrigatórias

1. Um snapshot DEVE capturar versões consistentes ou declarar componentes não consistentes; nunca fingir atomicidade que não existe.
2. Restauro DEVE iniciar a Mind em `RECOVERING`, revogar leases temporários e reconciliar `ToolCall`/`Action` `UNKNOWN` antes de novas ações.
3. Snapshots DEVEM ser cifrados, autenticados, sujeitos a retenção e testados com restauro isolado.
4. A exportação do utilizador aplica ACL, redação e políticas de terceiros; dados de cofre nunca são exportáveis.
5. O estado emocional/interacional é representado como contexto operacional temporário, não como alegação de sentimento real.

## Casos limite e erro

- **Snapshot corrompido:** marcar `CORRUPT`, não restaurar parcialmente sem plano explícito; usar último `VERIFIED`.
- **Ação estava a enviar email no reinício:** manter `UNKNOWN`, consultar o provedor e só depois decidir se há nova ação.
- **Esquema mudou:** executar migração versionada ou restauro de compatibilidade; não desserializar campos desconhecidos como permissivos.
- **Uma semana desligada:** reavaliar prazos, credenciais, schedules e crenças expiradas antes de declarar `READY`.

## Justificação

Persistir apenas “memórias” não restaura uma entidade operacional. O Mind State preserva a continuidade de identidade, intenção, foco, mundo conhecido e trabalho pendente, sem converter segredos ou raciocínio efémero em dados duradouros.

## Expansões futuras

Snapshots incrementais, recuperação entre regiões, ramificações para simulação, migração para outro fornecedor e atestação criptográfica de estado.

