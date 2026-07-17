# Aurora Platform — Execution Trace Specification: VS-001 Resurrection Test

**Estado:** Autorizado por ADR-001  
**Caso de utilização:** criar entidade → executar ciclo → shutdown → reiniciar → restaurar → continuar

## Objetivo

Provar que a Aurora é dona de estado além de uma execução ou sessão. VS-001 não introduz ferramentas, LLM externo ou conhecimento vetorial. Implementa apenas Entity Runtime Context, lifecycle persistente, Mind State mínimo e recuperação idempotente.

## Fluxo normativo

```text
CreateEntity(entity_001, assistant-v1)
  → EntityCreated → EntityRuntimeContextLoaded
  → VS-000("Olá") → Mind State persistido
  → ShutdownEntity → SnapshotVerified → EntityStateChanged(STOPPED)
  → processo termina
  → StartEntity(entity_001) → SnapshotLoaded → EntityStateChanged(READY)
  → VS-000("Quem sou eu?") → resposta baseada no contexto da entidade
```

## Contratos mínimos

```text
Entity
  entity_id, genome_id, created_at, lifecycle_state, mind_state_version

EntityRuntimeContext
  entity_id, genome_id, lifecycle_state, active_session_id?, snapshot_ref
  restored_at?, trace_id
```

Todos os objetos criados pelo ciclo transportam `entity_id`. A relação obrigatória é `Entity → Session → Trace`; uma session pode expirar sem alterar a Entity.

## Passos e eventos

| Passo | Persistência | Evento |
| --- | --- | --- |
| Criar | `Entity(CREATED)` e Mind State inicial | `EntityCreated` |
| Arrancar | lifecycle `RECOVERING → READY` | `EntityRuntimeLoaded`, `EntityStateChanged` |
| Executar mensagem | VS-000 com `entity_id` | eventos VS-000 enriquecidos |
| Encerrar | snapshot/Mind State + lifecycle `STOPPED` | `EntityShutdown`, `EntityStateChanged` |
| Restaurar | carregar Entity/Mind State e reconciliar | `EntityRestored`, `EntityStateChanged` |
| Continuar | novo Signal numa nova Session | `SignalReceived` com mesmo `entity_id` |

## Critérios de aceitação

1. A primeira execução cria Entity e Memory ancorada na Entity, Session, Signal e Observation.
2. Shutdown persiste um Mind State verificável; nenhum ciclo aberto fica silenciosamente perdido.
3. Um novo processo recupera a mesma `entity_id`, `genome_id`, memória e historial sem depender da Session anterior.
4. Todos os eventos/traces dos dois processos mostram a mesma `entity_id` e sessões distintas.
5. “Quem sou eu?” devolve uma resposta determinística que identifica a Entity, não inventa consciência ou biografia.
6. Repetir start/shutdown é idempotente e auditável.

## Falhas

- Snapshot em falta/corrompido: lifecycle fica `RECOVERING`/`FAILED`; não entra em `READY`.
- `entity_id` desconhecida em modo restore: erro seguro sem criar entidade implicitamente.
- Mind State incompatível: aplicar migração aprovada ou abortar com diagnóstico.

