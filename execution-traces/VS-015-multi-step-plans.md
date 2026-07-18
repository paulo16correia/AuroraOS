# VS-015 — Planos Multi-passo Controlados

## Objetivo

Evoluir o plano persistente de uma única tarefa interna para uma cadeia de tarefas ordenadas, com dependências explícitas e recuperação após reinício.

## Fluxo

```text
Goal(ASSIST_USER)
      |
      v
Plan(ASSISTANCE_PLAN)
      |
      +--> Task #1 INTERNAL_ANALYSIS ── COMPLETED
                                      |
                                      v
      +--> Task #2 PROVIDE_RECOMMENDATION ── COMPLETED
                                      |
                                      v
                              Capability / Policy / Approval
```

## Alterações ao modelo

`Task` passa a transportar `sequence` e `dependency_refs`. Ambos são persistidos, imutáveis em cada versão da task e incluídos no snapshot da Mind.

## Regras obrigatórias

1. Uma task só pode entrar em `RUNNING` se todas as dependências estiverem `COMPLETED`.
2. A criação de uma task é idempotente pelo seu identificador determinístico.
3. Restaurar uma entidade não recria nem repete tasks já concluídas.
4. A capability de email continua ancorada na task de análise original, preservando o identificador e a idempotência dos pedidos existentes.
5. Este slice não cria ações externas adicionais; mantém a fronteira Policy → Approval → Executor.

## Estados

```text
READY -> RUNNING -> COMPLETED
  |        |
  +--------+--> FAILED  (reservado para executor futuro)
```

## Casos limite

- Uma segunda mensagem para o mesmo plano apenas carrega as tasks existentes.
- Se a primeira task ainda não tiver terminado, a segunda não é criada.
- Uma base de dados anterior sem `sequence` ou `dependency_refs` mantém compatibilidade através dos defaults do contrato.

## Critérios de aceitação

- A entidade persiste as duas tasks e a dependência `Task #2 -> Task #1`.
- Shutdown/restore preserva a cadeia.
- O fluxo de email e a política de capabilities continuam idempotentes.
- Todos os testes de regressão permanecem verdes.

## Expansões futuras

- ramos condicionais, tasks paralelas e junções;
- estado `WAITING_FOR_APPROVAL` ligado a uma capability;
- recuperação de tarefas interrompidas;
- planos gerados pelo provider linguístico como proposta validada pelo Kernel.
