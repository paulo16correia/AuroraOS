# LAW-002 — A Mind nunca comunica diretamente com ferramentas

## Enunciado

A Mind representa estado; não executa efeitos. Toda a intenção que possa usar uma ferramenta percorre `Decision Engine → Policy/Permission Engine → Execution Layer → Tool Manager`.

## Aplicação

```text
Mind state → Decision → autorização → Action/ToolCall → Tool Manager → conector
```

## Controlo verificável

- Só o Tool Manager pode possuir manifests de conector e leases do cofre.
- `MindService` não expõe métodos de rede, shell, email ou credenciais.
- Auditoria deve relacionar cada `ToolCall` com `Decision`, política e aprovação quando exigida.

## Exceções

Não existem. Uma consulta de saúde é observação de infraestrutura e não uma chamada da Mind a uma ferramenta.

## Justificação

Evita lógica de negócio e efeitos externos escondidos dentro de memória ou modelos de conhecimento.

