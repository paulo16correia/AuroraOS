# ADR-008 — Interface de Executor com resultados sem efeito externo

**Estado:** ACEITE  
**Data:** 2026-07-18  
**RFCs afetadas:** RFC 021, RFC 040, RFC 051, ADR-006, ADR-007, LAW-002, LAW-003, LAW-006

## Contexto

O Kernel já representa a intenção externa como `CapabilityRequest`, mas falta um contrato explícito para o componente que, no futuro, poderá transformar um pedido autorizado num efeito observável. Ligar já um executor real misturaria interface, política e integração externa.

## Decisão

VS-008 introduz `Executor` e `CapabilityResult`. O Kernel despacha a request validada para um executor de referência sem efeitos (`NoopExecutor`). Este executor não possui credenciais, rede, ficheiros, plugins, providers nem chamadas de ferramentas; devolve apenas `NOT_IMPLEMENTED` ou `DRY_RUN`.

```text
CapabilityRequest → Executor Interface → CapabilityResult
```

`CapabilityResult` é persistido, auditável e não altera o estado da request. Nenhum resultado VS-008 representa envio, execução, sucesso externo ou autorização.

## Consequências

- A fronteira de execução passa a existir e é testável sem abrir acesso ao exterior.
- A Aurora pode distinguir capacidade indisponível de executor ainda não implementado.
- Integrações futuras devem implementar o mesmo contrato, atrás de política e aprovação.
- O Kernel continua a ser dono de contexto, estado, memória, objetivos e decisões; executores são consumidores limitados de requests.

## Migração e reversão

Não existem migrações destrutivas. Requests existentes passam a poder receber um resultado de referência no próximo ciclo elegível. Remover VS-008 impede a criação de novos resultados, preservando a auditoria existente.
