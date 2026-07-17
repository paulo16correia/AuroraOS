# Aurora Platform — Execution Trace Specification: VS-000

**Estado:** Congelado para implementação  
**Caso de utilização:** mensagem local → resposta textual → persistência, evento e auditoria  
**Fora de âmbito:** conector externo, LLM real, browser, scheduler, objetivos, capacidade e autonomia.

## 1. Objetivo e critério de sucesso

VS-000 valida o caminho mínimo completo da plataforma sem efeitos externos: uma mensagem autenticada entra na Platform, torna-se Signal, atravessa Kernel e Mind, produz uma decisão de resposta, devolve texto, regista observação/reflexão, persiste estado permitido e publica eventos auditáveis.

O slice é aceite quando o mesmo input, estado inicial e relógio controlado produzem o mesmo trace lógico; quando o processo reinicia, os registos persistidos e o diário de auditoria continuam disponíveis; e quando cada Lei aplicável é demonstrada por teste.

```text
InputMessage → Event → Signal → Kernel → Context → Attention → Working Memory
      → Mind/Reasoning → Thought → Decision(RESPOND) → OutputMessage
      → Observation → Reflection → MindChangeSet → Persistência → Event Bus → Audit
```

## 2. Limites de implementação

- O canal é `local_console`; o principal é um utilizador autenticado de teste.
- O Reasoning Provider é determinístico e local: devolve uma resposta configurada ou eco seguro; não há chamada direta a LLM nem rede.
- `CapabilityRequest` e `ToolCall` não são criados neste slice. VS-001 introduz a primeira capacidade (`CurrentTime`).
- Uma mensagem não cria memória duradoura automaticamente. Só uma mensagem classificada como preferência/facto explícito pode originar `MemoryCandidate`, validado pela política e LAW-001.
- A resposta é observação interna de comunicação, não sucesso de ferramenta externa.

## 3. Participantes e propriedade

| Participante | Responsabilidade neste trace |
| --- | --- |
| Local Gateway / Perception | valida principal, normaliza mensagem e cria evento de entrada. |
| Signal Router | classifica estímulo e publica `SignalReceived`. |
| Aurora Kernel | abre ciclo, fornece correlação, lifecycle, despacho e auditoria. |
| Mind Manager | abre Context/Working Memory e aplica apenas `MindChangeSet` validado. |
| Attention | seleciona evento atual e memória autorizada dentro do orçamento. |
| Reasoning Interface | recebe `Context` imutável e devolve `Thought`; provider nunca persiste estado. |
| Decision Engine | valida proposta e cria `Decision(RESPOND)`. |
| Event Bus | publica eventos através de outbox transacional. |
| Audit Manager | grava trace encadeado por hash e logs estruturados redigidos. |

## 4. Contratos mínimos

```text
InputMessage
  message_id, session_id, principal_id, channel, text, received_at

Context
  context_id, cycle_id, signal_ref, attention_set_ref, working_memory_ref
  memory_refs[], policy_version, token_budget, expires_at

Thought
  thought_id, cycle_id, intent: CONVERSATION, evidence_refs[]
  proposed_mode: RESPOND, response_outline, uncertainty[], status: VALIDATED

Decision
  decision_id, cycle_id, mode: RESPOND, thought_ref
  status: COMMITTED, policy_refs[], expiry_at

OutputMessage
  output_id, correlation_id, channel, text, decision_ref, delivered_at

Observation
  observation_id, kind: RESPONSE_GENERATED, source_ref, outcome: SUCCESS
  observed_at, evidence_refs[]

Reflection
  reflection_id, cycle_id, outcome: SUCCESS, lessons[], status: ACCEPTED
```

Todos os contratos são imutáveis, incluem `correlation_id` direta ou indiretamente e têm esquema versionado. Alterações incompatíveis exigem ADR.

## 5. Trace normativo

| Passo | Entrada | Operação | Saída persistida | Evento emitido | Limite inicial |
| --- | --- | --- | --- | --- | --- |
| 1. Receção | `InputMessage` | validar sessão, tamanho, canal e classificação | `DomainEvent` na outbox | `InputReceived` | 50 ms |
| 2. Perceção | `InputReceived` | normalizar texto e metadados; rejeitar conteúdo/sessão inválida | `Observation(RAW)` | `MessagePerceived` | 50 ms |
| 3. Sinal | observação validada | classificar `MESSAGE`, urgência baixa por defeito | `Signal` | `SignalReceived` | 50 ms |
| 4. Ciclo | Signal | Kernel cria `WorkItem`/`CognitiveCycle` e correlation scope | `WorkItem`, `CycleStageRecord` | `CycleStarted` | 50 ms |
| 5. Contexto | ciclo + Signal | Attention seleciona Signal e memória autorizada; abre Working Memory | `AttentionSet`, `WorkingMemory`, `Context` | `ContextCreated` | 100 ms |
| 6. Raciocínio | Context imutável | provider determinístico propõe `Thought(RESPOND)` | `Thought` | `ThoughtCreated` | 100 ms |
| 7. Decisão | Thought + policy | validar modo, expiração e saída segura | `Decision(COMMITTED)` | `DecisionMade` | 50 ms |
| 8. Saída | Decision | renderizar e devolver `OutputMessage` no gateway local | `OutputMessage` | `ResponseProduced` | 50 ms |
| 9. Observação | OutputMessage | registar resultado da geração/devolução local | `Observation(SUCCESS)` | `ObservationCreated` | 50 ms |
| 10. Reflexão | Observation | criar reflexão sem aprendizagem por defeito | `Reflection` | `ReflectionCompleted` | 100 ms |
| 11. Memória | Reflection + mensagem | apenas se candidata explícita e política permite: criar ChangeSet e aplicar | `MindChangeSet`, `Memory?` | `MemoryCandidateCreated`, `MindChangeApplied`, `MemoryStored?` | 100 ms |
| 12. Fecho | refs de todos os passos | selar Working Memory, concluir ciclo, confirmar outbox e audit chain | `CycleStageRecord`, `AuditRecord` | `CycleCompleted` | 100 ms |

O limite end-to-end alvo é 1 segundo em ambiente local; exceder o limite cria telemetria de degradação, não altera o resultado nem ignora auditoria.

## 6. Regras de persistência e eventos

1. A escrita de cada agregado e o respetivo evento de domínio ocorrem pela outbox na mesma transação.
2. Eventos são entregues pelo menos uma vez; consumidores devem ser idempotentes por `event_id`.
3. A Mind só recebe `MindChangeSet` no passo 11. Se não houver memória candidata válida, nenhum estado cognitivo duradouro é alterado.
4. `WorkingMemory` é selada e descartada no final, salvo referência de curta retenção explicitamente permitida.
5. Logs e AuditRecords contêm hashes, IDs e resumos seguros; nunca texto confidencial completo por defeito.

## 7. Falhas e comportamento esperado

| Condição | Comportamento |
| --- | --- |
| sessão/principal inválido | rejeitar antes de Perceção; publicar apenas evento de segurança seguro. |
| input repetido | devolver output/estado já associado à `idempotency_key`; não criar segundo ciclo. |
| provider falha/timeout | `Cycle FAILED`, `ReasoningFailed`, resposta segura de indisponibilidade; nenhuma memória criada. |
| persistência/outbox falha | não entregar output como concluído; rollback do agregado ou estado `FAILED` auditável. |
| Event Bus indisponível | outbox persiste; conclusão aguarda publicação/retry, sem perder causalidade. |
| classificação de memória falha | descartar candidato e concluir resposta; erro não bloqueia conversa. |
| cancelamento | selar/descartar Working Memory; não emitir `CycleCompleted`; emitir `CycleCancelled`. |

## 8. Observabilidade e replay

Cada trace deve poder ser consultado por `correlation_id` e mostrar:

```text
Session → InputReceived → SignalReceived → ContextCreated → ThoughtCreated
→ DecisionMade → ResponseProduced → ObservationCreated → ReflectionCompleted
→ (MemoryStored?) → CycleCompleted
```

O Replay Engine recebe `InputMessage`, snapshots de estado autorizados, relógio fixo e provider determinístico. Reexecuta em modo sem efeitos e compara: sequência/tipos de eventos, transições de estado, `Decision.mode`, hashes de saída e ChangeSets. Diferenças produzem `ReplayDiverged`; não reescrevem estado nem publicam em canais reais.

## 9. Casos de aceitação VS-000

1. Pergunta simples produz uma resposta e trace completo sem Memory duradoura.
2. “Lembra-te: prefiro respostas curtas” cria `MemoryCandidate`, valida relações/origem e persiste uma Memory ativa apenas se política explícita permitir.
3. Reinício após passo 8 preserva audit/outbox e retoma os passos pendentes de modo idempotente.
4. Repetir o mesmo input não duplica output, memória, eventos de efeito ou ciclo.
5. Replay com mesmo estado/relógio reproduz a mesma `Decision(RESPOND)` e sequência lógica de eventos.
6. Tentativa de chamar provider diretamente fora da `Reasoning Interface` falha o teste de arquitetura.

## 10. Não objetivos

VS-000 não mede qualidade de linguagem, não implementa conhecimento, não usa modelo externo e não demonstra autonomia. Demonstra que o Kernel consegue coordenar um ciclo cognitivo mínimo, persistente, auditável e recuperável.

