# Aurora Platform — RFC 090: Architecture Review Gate

**Estado:** Obrigatório antes de implementação de produto · **Depende de:** RFC 000–060

## Objetivo

Definir a revisão que congela a arquitetura suficientemente para iniciar código. Não é uma declaração de perfeição; é uma decisão informada de que as interfaces, invariantes e responsabilidades são estáveis o bastante para implementar e evoluir com ADRs.

## Processo

```text
inventário de RFCs → matriz de responsabilidades → análise de dependências
        │                         │                         │
        ▼                         ▼                         ▼
 testes de Leis/Constituição → ameaças e dados → interfaces/eventos
        │                         │                         │
        └──────────────→ decisões/ADRs e riscos ←───────────┘
                                      │
                                      ▼
                         ACCEPT | ACCEPT WITH CONDITIONS | REWORK
```

## Checklist por componente

1. O que representa e qual é a sua única responsabilidade?
2. Quem é dono do estado, quem o pode alterar e como nasce/termina?
3. Qual é a interface pública e quais os contratos de entrada/saída?
4. Que eventos produz/consome e quais as garantias de idempotência?
5. Que Leis, artigos constitucionais e classificações de dados aplica?
6. Existe sobreposição, ciclo de dependência, bypass do Kernel ou acoplamento a fornecedor?
7. Como falha, recupera, é observado e testado?

## Estruturas e interfaces

```text
ArchitectureReview
  id, scope, rfc_versions[], findings[], risks[], adr_refs[]
  decision: ACCEPT|CONDITIONAL|REWORK, approvers[], reviewed_at

Review.run(scope) -> ArchitectureReview
Review.blockImplementation(finding) -> GateStatus
```

## Regras obrigatórias

1. Nenhum módulo de escrita externa entra em implementação sem revisão `ACCEPT` ou condição explicitamente aceite.
2. Achados críticos de segurança, dono de estado indefinido ou violação de Lei bloqueiam o gate.
3. O output é uma baseline versionada; alterações posteriores usam ADR e testes de regressão.

## Justificação e expansões

Esta fase impede que a documentação vire apenas inspiração. A revisão transforma RFCs em contratos de implementação e revela sobreposições antes de elas se tornarem código. Futuramente pode incluir revisão independente e certificação de plugins.

