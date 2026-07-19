# VS-022 — Entity resolution

## Objetivo

Permitir que uma entidade Aurora resolva referências persistentes do utilizador, sem pedir um endereço de email em cada instrução.

```text
Afirmação explícita do utilizador
        ↓
Contact / ContactGroup persistido
        ↓
Pedido: “envia um email ao João”
        ↓
RecipientResolution
        ↓
EmailDraft(validado)
        ↓
Policy → Approval → Executor
```

## Objetos

`Contact` contém nome apresentado, aliases normalizados, endereços de email, proveniência, estado e versão. `ContactGroup` contém aliases e a lista explícita de membros. `RecipientResolution` regista a referência original, endereços resolvidos, objetos de origem, estado e razão.

## Regras obrigatórias

- Apenas uma afirmação explícita do utilizador cria ou atualiza contactos.
- O LLM nunca cria, corrige ou escolhe contactos.
- “O João é joao@example.com” e “O Pedro tem o email pedro@example.com” são afirmações aceites.
- “A minha equipa inclui ana@example.com, bruno@example.com” cria ou atualiza o grupo persistente.
- Referências sem correspondência ficam `UNRESOLVED`; correspondências múltiplas ficam `AMBIGUOUS`. Ambas bloqueiam o `EmailDraft` antes de aprovação.
- Contactos, grupos e resoluções pertencem à Entity, sobrevivem a reinícios e entram no snapshot da Mind.

## Limites

Não há importação automática de contactos externos, pesquisa na internet ou inferência de identidades. A integração com Google Contacts, sincronização, permissões por contacto e desambiguação conversacional são expansões futuras.
