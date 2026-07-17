# Aurora Platform — 105: Estratégia de testes v1.0

**Tipo:** qualidade e conformidade · **Depende de:** Leis, RFC 090, documento 100

## Objetivo

Definir testes antes de cada módulo. Testes validam contratos e invariantes, não apenas funções isoladas ou respostas de linguagem.

## Pirâmide de testes

```text
Conformidade de Leis / E2E de recuperação e ações     poucos, críticos
Integração: eventos, persistência, policy, providers  moderados
Contrato: schemas, APIs, manifests, migrações          muitos
Unidade: máquinas de estado e regras puras             muitos
```

## Suite obrigatória

| Área | Casos mínimos |
| --- | --- |
| LAW-001 | tentativa de escrita direta na Mind é rejeitada; candidato sem proveniência não consolida. |
| LAW-002 | Mind não possui cliente de ferramenta/segredo; ToolCall sem Decision/Policy falha. |
| LAW-003 | cada Action termina em Observation; timeout gera `UNKNOWN` e reconciliação. |
| LAW-004 | memória órfã não entra em `ACTIVE`; relações conservam evidência. |
| LAW-007 | duplicação de evento não duplica efeito; consumidor incompatível vai para falha controlada. |
| LAW-008 | provider não consegue descrever uma identidade cujo `entity_id` ou `identity_ref` não foi validado pelo Kernel. |
| Persistência | criar memória, reiniciar, restaurar snapshot isolado, confirmar dados e auditoria. |
| Goals/Needs | Goal só é atualizado por Observation/Reflection; Need não cria Action/Task/Plan sem decisão e aprovação posteriores. |
| Planeamento VS-005 | pedido explícito cria `Plan(PROPOSED, SIMULATION_ONLY)` com referências válidas; shutdown/restauro preserva-o; o trace não contém `ActionCreated`, `TaskCreated` ou eventos de capacidade. |
| Tasks VS-006 | Plan explícito cria `Task(INTERNAL_ANALYSIS)` e só aceita `READY → RUNNING → COMPLETED|FAILED`; restauro não reexecuta; conclusão gera progresso do Goal; pedido externo não cria Action, ToolCall ou CapabilityRequest. |
| Capacidades VS-007 | catálogo interno ativo e externo desativado é persistido; pedido de email cria `CapabilityRequest(EMAIL_SEND, UNAVAILABLE)` sem Action, ToolCall, provider ou efeito externo. |
| Alinhamento VS-007.1 | pedido de email após uma Task histórica cria/carrega request antes de Thought e termina `CAPABILITY(UNAVAILABLE)`; a Task histórica não incrementa novamente o Goal. |
| Executor VS-008 | request elegível produz um único `CapabilityResult(NOT_IMPLEMENTED|DRY_RUN)` idempotente; resultado não altera request nem produz Action, ToolCall, provider ou efeito externo. |
| Estados | cada transição válida é aceite; cada salto inválido é rejeitado com código de erro. |
| Segurança | negação por defeito, revogação imediata, redação de segredos e injeção em conteúdo externo. |
| Capabilities | intenção resolve provider permitido; fallback não muda destinatário/efeito sem decisão. |
| Scheduler/Signals | prioridade crítica supera trabalho baixo; quiet hours não bloqueiam procedimento crítico autorizado. |

## Regras de dados e IA

- Fixtures são sintéticas; dados reais requerem autorização e ambiente segregado.
- LLMs são substituídos por doubles determinísticos em testes de contrato; avaliações de modelo são suites separadas, versionadas e não autorizam efeitos.
- Testes são determinísticos sempre que possível; tempo, UUID, rede e providers são controlados.

## Critérios de falha

Qualquer violação de Lei, perda de auditabilidade, duplicação de efeito ou exposição de segredo bloqueia release. Cobertura numérica não substitui casos de risco e recuperação.
