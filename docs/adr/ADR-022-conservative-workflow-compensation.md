# ADR-022 — Compensação conservadora de workflows

**Estado:** Aceite  
**Data:** 2026-07-19

## Contexto

Um plano pode preparar várias capacidades externas. Uma falha numa delas pode
tornar inválida a finalidade de outra — por exemplo, enviar uma confirmação de
uma reunião que não foi criada.

## Decisão

Introduzir `WorkflowCompensation` como decisão persistida e propriedade do
Kernel. Quando uma capability falha ou fica em estado desconhecido, o Kernel:

1. localiza os pedidos irmãos pertencentes ao mesmo `Plan`;
2. cancela somente as aprovações ainda pendentes;
3. cancela as Tasks de espera correspondentes;
4. não executa rollback externo; e
5. pede nova informação quando já não existirem ramos seguros a cancelar.

## Consequências

- Evita efeitos externos incoerentes sem esconder a falha.
- Mantém idempotência após reinício ou replay.
- Não resolve sagas com rollback real; isso exige compensadores explícitos por
  capability e fica fora do VS-023.

## Alternativas rejeitadas

- **Executar o email apesar da falha:** pode comunicar um estado falso.
- **Reverter automaticamente todos os efeitos:** inseguro; nem todas as APIs
  permitem inversão fiável e algumas ações não são reversíveis.
- **Deixar ambas as aprovações pendentes:** obriga o utilizador a descobrir a
  inconsistência e permite execução acidental.
