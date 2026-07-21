# ADR-021 — Proveniência explícita para contactos

## Decisão

O diretório de contactos da Aurora só aprende através de afirmações explícitas do utilizador. Cada contacto e grupo regista `provenance = USER_ASSERTION`.

## Justificação

Resolver uma pessoa para o destinatário errado é um risco de execução externo. O modelo de linguagem e inferências implícitas não são fontes de verdade suficientes para essa decisão.

## Consequências

O sistema pode dizer que não conhece “João” em vez de adivinhar. Quando existe uma resolução, esta é auditável, persistente e ligada a uma Entity concreta. A qualidade conversacional futura pode evoluir sem enfraquecer a fronteira de segurança.
