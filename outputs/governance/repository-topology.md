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

## Migração prática

O repositório atual funciona temporariamente como `aurora-spec`. Antes do primeiro código de produto, mover `outputs/` para a raiz de `aurora-spec` e criar `aurora-kernel` e `aurora-apps` vazios com CI de contratos. Esta reorganização é estrutural, não altera a baseline congelada.

