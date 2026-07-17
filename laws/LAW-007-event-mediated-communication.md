# LAW-007 — Comunicação mediada por eventos

## Enunciado

Os componentes da Platform comunicam mudanças de estado e reações assíncronas através do Event Bus. Chamadas diretas só são permitidas para consultas síncronas publicadas por contrato e não podem criar efeitos em cascata ocultos.

## Controlo verificável

- Cada produtor declara eventos; cada consumidor declara subscrições e versão de esquema.
- Eventos possuem `event_id`, `correlation_id`, produtor, data, classificação e chave de idempotência quando aplicável.
- Consumidores são idempotentes e registam entrega/processamento.

## Exceções

Consultas de leitura restrita, verificação de saúde e comandos do Kernel por interface autenticada. Mesmo nesses casos, mudanças resultantes publicam eventos.

## Justificação

O barramento reduz acoplamento, torna reações observáveis e permite que UI, auditoria, reflexão e outros consumidores reajam sem dependências escondidas.

