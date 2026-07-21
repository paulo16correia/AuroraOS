# ADR-020 — Aprovação independente para branches de workflow

## Decisão

Um workflow com várias capabilities de escrita mantém uma `ApprovalRequest` por preparação, em vez de uma aprovação implícita para todo o workflow.

## Justificação

Calendar e Email têm efeitos e riscos diferentes. A aprovação deve identificar a capability a autorizar e sobreviver a reinícios sem perder essa associação.

## Consequências

O utilizador pode aprovar uma branch de cada vez ou usar uma confirmação explícita para todas. A UI futura poderá oferecer controlos individuais sem alterar o contrato do Kernel.
