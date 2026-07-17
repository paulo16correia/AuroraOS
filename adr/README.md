# ADR — Architecture Decision Records

Os ADRs registam alterações à baseline congelada. Uma RFC é o contrato estável; um ADR explica por que motivo, como e a partir de que versão esse contrato muda.

## Formato obrigatório

```text
ADR-<número>-<nome>.md
Estado: PROPOSTO | ACEITE | REJEITADO | SUBSTITUÍDO
Data, decisores, RFCs afetadas
Contexto, decisão, alternativas, consequências
Migração, compatibilidade, testes e plano de reversão
```

## Processo

1. Abrir ADR com problema concreto e evidência de implementação/revisão.
2. Avaliar Constituição, Leis, segurança, estado, eventos e dependências.
3. Aceitar ou rejeitar explicitamente.
4. Só um ADR `ACEITE` autoriza alterar RFCs, código ou migrações afetadas.

