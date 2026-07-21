# Aurora Platform — Topologia de repositórios

## Decisão

Separar especificação, Kernel e Applications para impedir que implementação ou conectores alterem silenciosamente a arquitetura.

```text
aurora-spec
  RFCs, Leis, Constituição, ADRs, testes de conformidade e baseline de contratos

aurora-kernel
  Aurora Kernel, Mind Manager, State Manager, Event Bus, Policy, auditoria,
  contratos publicados, migrations e implementação de referência

aurora-apps
  Applications/providers: email, Discord, browser, GitHub, calendário, etc.
  Cada provider depende apenas do SDK/contratos publicados pelo Kernel
```

## Regras de versionamento

- `aurora-spec` publica versões de baseline e contratos; é fonte normativa.
- `aurora-kernel` declara a versão mínima/máxima de spec suportada.
- `aurora-apps` declara a versão de SDK/Capability e não importa módulos internos da Mind.
- Alteração incompatível percorre `ADR → spec → kernel SDK → apps`, com testes de compatibilidade.

## Estado atual

Este repositório é o `aurora-spec`: os RFCs e a documentação normativa vivem na raiz, enquanto os documentos agrupados vivem em diretórios próprios (`adr/`, `laws/`, `governance/`, `reviews/` e `execution-traces/`). O `aurora-kernel` é mantido como repositório independente. Esta organização é estrutural e não altera a baseline congelada.
