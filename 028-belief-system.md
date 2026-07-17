# Aurora OS — RFC 028: Sistema de crenças e incerteza

**Estado:** Normativo · **Depende de:** RFC 03, 040–041

## Objetivo

Representar generalizações e previsões revisáveis que orientam atenção e decisão, sem as confundir com factos confirmados. Uma crença é uma hipótese operacional com evidência, confiança, âmbito e prazo de revisão.

## Arquitetura e fluxo

```text
Observations + Memories + Experiences
                 │
                 ▼
       inferência de padrão / proposta de crença
                 │
                 ▼
  evidência, contraevidência, confiança e validade
                 │
                 ▼
            Belief ativo → atenção/decisão (nunca fonte única de alto risco)
```

## Estruturas de dados

```text
Belief
  id, subject_ref, predicate, object_json, scope_json
  confidence: 0..1, evidence_for_refs[], evidence_against_refs[]
  basis: OBSERVED|INFERRED|USER_STATED|IMPORTED
  status: CANDIDATE|ACTIVE|CHALLENGED|SUPERSEDED|RETRACTED|EXPIRED
  valid_from, review_at, last_evaluated_at, decision_impact: LOW|MEDIUM|HIGH

BeliefUpdate
  id, belief_id, observation_ref, delta_confidence, reason, applied_at
```

Exemplos: “o utilizador prefere respostas curtas” ou “o servidor A tem maior estabilidade observada”. Uma crença sobre uma pessoa não autoriza inferências sensíveis, decisões discriminatórias ou comunicação externa sem evidência adequada.

## Interfaces

```text
Beliefs.propose(candidate, evidence) -> Belief
Beliefs.observe(belief_id, observation) -> BeliefUpdate
Beliefs.query(scope, threshold, access_context) -> Belief[]
Beliefs.challenge(belief_id, evidence) -> Belief
```

## Regras obrigatórias

1. Crenças DEVEM declarar evidência a favor e suporte de confiança; o modelo sozinho não é evidência.
2. Nunca podem ser a única base para ação de alto risco, identidade, segurança, dinheiro, saúde, direito ou conteúdo sensível.
3. Crenças têm data de revisão/expiração e diminuem de confiança quando não são confirmadas, segundo política explícita.
4. Declarações diretas e verificadas do utilizador podem prevalecer, mas continuam corrigíveis.

## Casos limite e erro

- **Evidência contraditória:** estado `CHALLENGED`; reduzir ou separar âmbito, em vez de média cega.
- **Dados insuficientes:** manter `CANDIDATE`; não personalizar de forma material.
- **Previsão falhada:** anexar contraevidência e reavaliar, não apagar silenciosamente.

## Justificação

Separar crença de memória permite que a Aurora use padrões úteis sem fingir que são realidade. Isto reduz rigidez e impede que inferências transitórias se tornem factos permanentes.

## Expansões futuras

Modelos bayesianos, calibração automática, explicações de confiança e avaliação de enviesamento.

