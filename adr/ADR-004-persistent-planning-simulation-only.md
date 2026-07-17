# ADR-004 — Planeamento persistente, deliberativo e apenas em simulação

**Estado:** ACEITE  
**Data:** 2026-07-17  
**RFCs afetadas:** RFC 05, RFC 021, RFC 022, RFC 031, RFC 040, LAW-006

## Contexto

VS-004 introduziu continuidade operacional: a Entity possui Goal, EntityState e Need, mas ainda não consegue representar uma hipótese de trabalho futura. Criar uma `Action` neste ponto confundiria uma necessidade interna com autorização para agir.

## Decisão

VS-005 introduz `Plan` persistente como proposta deliberativa. Um pedido explícito do utilizador — inicialmente, “Como poderias ajudar-me melhor?” — pode materializar o encadeamento `Goal → Need → Plan(PROPOSED)`. O primeiro plano é `ASSISTANCE_PLAN`, referenciando o Goal `ASSIST_USER` e a Need `UNDERSTAND_USER`.

O plano contém somente os passos `ASK_CLARIFICATION` e `PROVIDE_RECOMMENDATION`, ambos `PENDING` e `SIMULATION_ONLY`. Não existem `Action`, `Task`, `ToolCall`, execução externa, agendamento ou alteração automática do estado para `REVIEWED`, `APPROVED` ou `COMPLETED`.

## Consequências

- A Need continua a ser evidência e justificação; não cria efeitos por si própria.
- O Kernel é o único criador e leitor de Plans no slice. O provider recebe um Plan já validado, não o inventa.
- Um Plan é preservado no snapshot de shutdown e pode ser carregado numa sessão posterior.
- O trace passa a incluir `PLANNING`, `PLAN_CREATED` ou `PLAN(LOADED)`; os eventos correspondentes são `PlanCreated` e `PlanLoaded`.
- A passagem para aprovação e execução requer slice e ADR posteriores, incluindo política, Capability Boundary e Observation obrigatória.

## Migração e reversão

Não há migração de dados: Entities existentes não recebem Plans automaticamente. Remover VS-005 desativa a proposta e a exposição de Plans, mas preserva objetos e eventos para auditoria.
