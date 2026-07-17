# ADR-003 — Goals e ciclo de agência controlada

**Estado:** ACEITE  
**Data:** 2026-07-17  
**RFCs afetadas:** RFC 05, RFC 031, RFC 033, RFC 040, RFC 043, LAW-006

## Contexto

VS-003 provou que uma Entity preserva factos externos entre sessões. Falta persistir o motivo operacional que orienta a continuidade: objetivos ativos, progresso verificável, estado operacional e necessidades explícitas.

## Decisão

VS-004 cria, no nascimento da Entity, um Goal contínuo `ASSIST_USER` derivado do propósito aprovado na `Identity`, um `EntityState` operacional e uma Need `UNDERSTAND_USER`. Em cada ciclo concluído, o Kernel avalia o Goal usando Observation/Reflection e atualiza o estado. Needs são apenas propostas internas: não criam Actions, Tasks, Plans, mensagens ou chamadas externas.

## Consequências

- Um provider só recebe Goals ativos carregados pelo Kernel e só pode descrevê-los quando foram persistidos e validados para a Entity ativa.
- O progresso VS-004 é um contador de evidências de assistência, não uma percentagem de conclusão. Um Goal contínuo permanece `ACTIVE` mesmo depois de acumular evidência.
- A versão inicial não cria Goals a partir de conversa, não agenda trabalho nem executa ferramentas. A criação de Goals adicionais exige um slice posterior e pedido/approval explícito.

## Migração e reversão

Entidades VS-001–VS-003 recebem Goal, EntityState e Need de compatibilidade auditada no primeiro arranque VS-004. A reversão desativa a avaliação e a exposição dos objetos, mas preserva-os para auditoria; não elimina Identity, memória ou histórico existente.
