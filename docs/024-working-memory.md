# Aurora OS — RFC 024: Memória de trabalho

**Estado:** Normativo · **Depende de:** RFC 03, 021, 023, 040

## Objetivo

Fornecer o espaço temporário, isolado e limitado onde um ciclo mantém rascunhos, contexto selecionado, resultados intermédios e decisões pendentes. Não é uma base vetorial, não é uma conversa sem fim e não é memória persistente por defeito.

## Arquitetura

```text
AttentionSet → ContextFrame (por ciclo) → rascunhos/resultados/hipóteses
                                           │
                                           ▼
                                  decisão de consolidação
                                  ├─ descartar
                                  ├─ anexar a auditoria
                                  └─ propor Memory/Experience
```

## Estruturas de dados

```text
WorkingMemory
  id, cycle_id, session_id, status: OPEN|SEALED|EXPIRED|DISCARDED
  capacity_tokens, capacity_items, sensitivity_ceiling, expires_at

WorkingItem
  id, working_memory_id, type: CONTEXT|DRAFT|HYPOTHESIS|RESULT|QUESTION
  payload_ref|payload_json, source_refs[], confidence, sensitivity
  created_at, expires_at, disposition: PENDING|DISCARD|AUDIT|CONSOLIDATE
```

## Interfaces

```text
WM.open(cycle_id, attention_set) -> WorkingMemory
WM.put(id, item) -> WorkingItem
WM.seal(id) -> WorkingMemory
WM.dispose(id, consolidation_decisions) -> DisposalReport
```

## Regras obrigatórias

1. Cada Working Memory DEVE pertencer a um ciclo; partilha entre ciclos exige transferência explícita de itens.
2. Tem TTL, limites de capacidade e teto de classificação; ao expirar, é selada e descartada/consolidada.
3. Hipóteses permanecem marcadas como tal e NÃO podem tornar-se factos sem o fluxo da RFC 03.
4. O conteúdo não deve ser exposto ao utilizador como “raciocínio interno”; saídas são sínteses operacionais aprovadas.

## Casos limite e erro

- **Capacidade esgotada:** Attention remove o item menos útil ou ciclo pede clarificação; não trunca silenciosamente dados sensíveis.
- **Reinício:** recuperar apenas objetos selados num armazenamento cifrado de curta duração; rascunhos abertos podem ser descartados conforme política.
- **Sessão longa:** abrir frames sucessivos com resumos citados, não prolongar um frame ilimitado.

## Justificação

Uma memória de trabalho explícita permite tratar uma tarefa complexa sem poluir memória duradoura e limita o raio de exposição do contexto.

## Expansões futuras

Frames para multimodalidade, snapshots de depuração autorizados e memória de trabalho colaborativa por tarefa.

