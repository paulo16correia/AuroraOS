# Aurora OS — RFC 025: Deliberação interna e síntese explicável

**Estado:** Normativo · **Depende de:** RFC 021–024

## Objetivo

Definir o estado interno de deliberação sem confundir telemetria de execução, cadeia de raciocínio privada ou comunicação ao utilizador. A Aurora deve conseguir coordenar passos internos e explicar decisões de forma verificável, sem alegar consciência.

## Arquitetura

```text
ContextFrame → DeliberationState → Thought → Decision
                    │                 │          │
                    ▼                 ▼          ▼
              estado interno      síntese       explicação operacional
              protegido           auditável     para utilizador
```

## Estruturas de dados

```text
DeliberationState
  id, cycle_id, phase: ORIENT|RETRIEVE|COMPARE|PLAN|DECIDE|VERIFY
  active_question, unresolved_questions[], candidate_refs[]
  assertions[], uncertainty[], next_step, status: ACTIVE|PAUSED|CLOSED
  trace_ref, retention_until

Thought
  id, cycle_id, intent, objective_ref, evidence_refs[]
  assumptions[], options[], uncertainty[], recommended_option
  user_explanation, status: DRAFT|VALIDATED|REJECTED|SUPERSEDED
```

`trace_ref` é material técnico protegido, com retenção limitada e acesso operacional estrito. `user_explanation` contém motivo, fontes e próximo efeito; não é uma transcrição de raciocínio detalhado.

## Interfaces

```text
Deliberation.start(cycle_id, question) -> DeliberationState
Deliberation.advance(id, evidence) -> DeliberationState
Deliberation.summarise(id) -> Thought
Deliberation.close(id, disposition) -> void
```

## Regras obrigatórias

1. Deliberação interna DEVE estar ligada a ciclo e prazo; não existe processo mental global sem proprietário.
2. Afirmações sem evidência permanecem hipóteses e têm confiança explícita.
3. O sistema NÃO DEVE afirmar “estou a pensar” como evidência de trabalho; só estados reais de `WorkItem` podem ser comunicados.
4. Dados sensíveis no trace devem ser minimizados, cifrados e excluídos de exportações normais.

## Casos limite e erro

- **Deliberação inconclusiva:** produzir `ASK`/`WAIT`, com perguntas concretas.
- **Trace indisponível:** manter decisão apenas se as fontes e política forem recuperáveis; caso contrário invalidar.
- **Pedido de cadeia interna:** fornecer explicação operacional e evidência, não trace privado.

## Justificação

Separar deliberação de explicação reduz riscos de privacidade e evita desenhar o produto em torno de raciocínios não determinísticos, preservando ainda observabilidade útil.

## Expansões futuras

Verificação formal de afirmações, revisores especializados e relatórios de decisão por domínio.

