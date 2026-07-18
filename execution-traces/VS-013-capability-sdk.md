# Aurora OS — Execution Trace Specification: VS-013 Capability SDK

**Estado:** Autorizado por ADR-013  
**Caso de utilização:** o Kernel resolve uma capability por contrato estável, sem conhecer SMTP, ficheiros ou APIs futuras.

## Objetivo

Transformar o executor de email numa primeira implementação de uma plataforma extensível. O Kernel coordena persistência, políticas, correlação e auditoria; cada executor implementa apenas o comportamento da sua capability.

## Contrato estável

```text
CapabilityExecutor
  capability_name: str
  executor_id: str

  prepare(CapabilityRequest, Capability, payload)
      -> ExecutionPreparation

  execute(ExecutionPreparation, ExecutionRecord, payload)
      -> ExecutionOutcome

  recover(ExecutionRecord)
      -> RecoveryDecision
```

`payload` é um contrato de domínio validado pelo Kernel. Em VS-013, `EMAIL_SEND` recebe `EmailDraft`; capabilities futuras podem introduzir payloads próprios sem alterar o ciclo cognitivo.

## Fluxo

```text
CapabilityRequest
  → CapabilityExecutorRegistry.resolve(capability_name)
  → executor.prepare(...)
  → ExecutionPreparation
  → Approval boundary
  → executor.execute(...)
  → ExecutionOutcome
  → CapabilityResult

Após restart:
  ExecutionRecord → executor.recover(...) → RecoveryDecision
```

## Ownership

| Componente | Responsabilidade |
| --- | --- |
| Kernel / Dispatcher | persistência, eventos, idempotência, Entity boundary, Approval boundary |
| Registry | associação capability → executor |
| Executor | semântica da operação, outcome e decisão de recuperação |
| Provider externo | I/O específico da integração |

Executores não recebem repositório, event bus, LLM, Mind ou permissões globais.

## Regras obrigatórias

1. O Kernel nunca contém condicionais específicas de fornecedores externos.
2. Um executor não escreve diretamente no estado da Aurora nem publica eventos.
3. Um executor só recebe payload validado e a Entity já confirmada pelo Dispatcher.
4. Cada capability tem no máximo um executor registado por runtime.
5. `recover` é obrigatório; a ausência de decisão de recuperação segura é erro de implementação.
6. `ExecutionOutcome` não é persistido pelo executor; o Dispatcher transforma-o em `ExecutionRecord` e `CapabilityResult`.
7. Capability sem executor resolve para fallback bloqueado, nunca para execução implícita.

## Migração EMAIL_SEND

`EmailSendExecutor` implementa o SDK completo. `SmtpEmailProvider` fica atrás desse executor. O Dispatcher deixou de conhecer como enviar email; chama apenas `execute` e persiste o outcome devolvido.

## Critérios de aceitação

1. O fluxo de email continua a produzir `SUCCESS`, `FAILED` e `UNKNOWN` com idempotência preservada.
2. Um provider controlado pode substituir SMTP nos testes sem alterar o Kernel.
3. Não existe acesso direto a provider externo fora de um executor.
4. Todos os testes VS-000–VS-012 permanecem verdes.

## Próximos passos

VS-014 introduzirá políticas transversais antes de `execute`. Novas capabilities, como `FILE_WRITE` ou `CALENDAR_CREATE`, serão novos executores que implementam este contrato, não novos caminhos no Kernel.
