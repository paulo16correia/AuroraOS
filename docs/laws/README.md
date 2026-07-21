# Leis da Aurora OS

As Leis são invariantes de arquitetura. Aplicam-se a todas as RFCs, serviços, conectores e versões do modelo. Uma implementação que viole uma Lei é não conforme, mesmo que tenha passado testes funcionais.

```text
Constituição → Leis → Políticas → Decisões → Ações
```

- A Constituição define valores e limites duradouros.
- As Leis definem invariantes estruturais verificáveis.
- As políticas concretizam as Leis por contexto e utilizador.
- Nenhuma política pode permitir uma violação de Lei.

Cada Lei deve ser aplicada por pelo menos um controlo de arquitetura, teste de conformidade e registo de auditoria.

- [LAW-008 — Integridade da identidade pelo Self Model](LAW-008-self-model-identity-integrity.md)
