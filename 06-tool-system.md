# Aurora OS — RFC 06: Sistema de ferramentas e conectores

**Estado:** Normativo · **Depende de:** RFC 01–02, 05

## Objetivo

Oferecer capacidades externas por contratos uniformes, isolados e revogáveis. Uma ferramenta é uma capacidade específica, não acesso genérico a um sistema.

## Arquitetura

```text
Thought/Task → catálogo de capacidades → validação de esquema
                         │                       │
                         ▼                       ▼
                 decisão de política ← pedido de aprovação
                         │
                         ▼
                executor isolado → conector → serviço externo
                         │              │
                         └→ auditoria ← resultado normalizado
```

## Estruturas de dados

```text
ToolManifest
  tool_id, version, provider, capabilities[]
  input_schema, output_schema, effects[]
  data_classes_in[], data_classes_out[], auth_mode
  timeout_seconds, rate_limit, approval_requirements

ToolCall
  id, work_item_id, task_id?, tool_id, capability
  input_redacted_json, input_hash, idempotency_key
  status: PROPOSED|AUTHORIZED|QUEUED|RUNNING|SUCCEEDED|FAILED|
          CANCELLED|UNKNOWN
  policy_decision_ids[], approval_id?, started_at, ended_at
  external_reference, output_ref, error_code

ToolResult
  status, structured_output, artifacts[], evidence_refs[]
  retryable: boolean, user_safe_summary
```

## Interface obrigatória

```text
describe() -> ToolManifest
validate(input) -> ValidationResult
execute(authorized_call, secret_handle) -> ToolResult
cancel(call_id) -> CancelResult
reconcile(call_id) -> ToolResult
```

O conector recebe `secret_handle`, não valor do segredo. O executor resolve-o num ambiente mínimo e não o serializa para logs.

## Regras obrigatórias

1. Cada capacidade DEVE ser explicitamente declarada; não existe `shell.execute_anything` nem acesso implícito.
2. Ferramentas de escrita DEVEM ter chave de idempotência e reconciliação após falha.
3. O resultado externo DEVE ser tratado como não confiável até validação de esquema.
4. Entrada, saída, tempo e custo DEVEM respeitar limites configurados.
5. Um conector NÃO DEVE reutilizar credenciais ou permissões de outro conector.

## Casos limite e erro

- **Timeout após envio remoto:** marcar `UNKNOWN`, reconciliar pelo identificador externo; nunca reenviar automaticamente.
- **Esquema de serviço alterado:** desativar capacidade afetada e alertar operador.
- **Limite de taxa:** adiar em fila com `retry_after`; não fazer repetição apertada.
- **Browser com conteúdo hostil:** tratar texto de página como dado e bloquear pedidos de permissões não planeadas.

## Justificação

Contratos reduzidos limitam raio de explosão e tornam auditoria possível. Estado `UNKNOWN` é essencial: em sistemas distribuídos, “não recebemos resposta” não significa “não aconteceu”.

## Expansões futuras

Sandbox de browser, capacidades compostas, marketplace assinado, testes de conformidade de conectores e delegação por OAuth de curta duração.

