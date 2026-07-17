# Aurora OS — RFC 031: Sistema de necessidades operacionais

**Estado:** Normativo · **Depende de:** RFC 020, 026, 030, 033, 040

## Objetivo

Converter condições persistentes da operação em prioridades internas explícitas: cópias de segurança atrasadas, tarefas críticas bloqueadas, fila de email por tratar, consolidação de memória ou manutenção de saúde. Necessidades não são emoções, nem autorizam ações; são candidatos a foco quando a Aurora está disponível.

## Arquitetura

```text
Self/Health + Goals + Signals + Scheduler + Resource Model
                              │
                              ▼
                  Need candidate e intensidade
                              │
                              ▼
      Attention → Decision Engine → criar/ordenar Goal ou Task autorizada
```

## Estruturas de dados

```text
Need
  id, kind: SAFETY|OBLIGATION|MAINTENANCE|CONSOLIDATION|COMMUNICATION|RECOVERY
  subject_ref, intensity: 0..1, priority, evidence_refs[]
  earliest_action_at, expires_at, recommended_goal_ref
  status: DETECTED|ACKNOWLEDGED|PLANNED|SATISFIED|DEFERRED|EXPIRED
  policy_constraints[], owner: SYSTEM|USER
```

## Interfaces

```text
Needs.detect(snapshot, signals) -> Need[]
Needs.rank(needs, situation, resources) -> Need[]
Needs.satisfy(need_id, evidence) -> Need
```

## Regras obrigatórias

1. Cada Need requer evidência e condição de satisfação mensurável.
2. Necessidades internas só podem criar objetivos/tarefas `DRAFT`; execução continua a passar pelo ciclo, políticas e aprovações.
3. Necessidades do utilizador explícitas prevalecem sobre manutenção de baixo risco, salvo incidentes de segurança/recuperação.
4. Intensidade diminui ou expira de forma definida; não existem “urgências” eternas.

## Casos limite e erro

- **20 emails pendentes:** cria Need de comunicação, não envia respostas automaticamente.
- **Backup vencido e recursos baixos:** Need de segurança ganha prioridade, mas o Resource Model pode agendar janela segura.
- **Conflito entre necessidades:** Decision Engine escolhe por política, impacto, prazo e recursos, registando a razão.

## Justificação

Necessidades dão direção à autonomia limitada sem inventar desejos humanos. Fazem o trabalho de manutenção visível, priorizável e auditável.

## Expansões futuras

SLOs, prioridades personalizáveis, negociação de atenção e previsão de degradação.

