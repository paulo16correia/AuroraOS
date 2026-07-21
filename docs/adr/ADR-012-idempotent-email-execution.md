# ADR-012 — Execução idempotente de EMAIL_SEND

**Estado:** Aceite  
**Data:** 2026-07-18  
**Decisão:** VS-012 — Idempotent Email Execution

## Contexto

Após VS-011, a Aurora consegue preparar e obter aprovação persistente. Falta transformar essa autorização numa capacidade externa real sem repetir envios em caso de falha, timeout ou reinício.

## Decisão

Implementar `EmailDraft`, `ExecutionRecord`, `SmtpEmailProvider` e o caminho `Approval(APPROVED) → Executor.execute_approved`. O record entra em `EXECUTING` antes de qualquer I/O. Nenhum record `EXECUTING` recuperado é reenviado automaticamente; é tratado como `UNKNOWN` até reconciliação.

O provider SMTP é configurado exclusivamente por variáveis de ambiente. Sem configuração completa, falha localmente e de forma auditável.

## Consequências

- A Aurora executa uma capacidade externa real sob aprovação e com auditoria completa.
- Idempotência é garantida no Kernel para operações concluídas ou ambíguas; o header de chave permite colaboração com providers que a suportem.
- O problema de entrega ambígua é preferido a um reenvio potencialmente duplicado.

## Alternativas rejeitadas

1. **Repetir automaticamente após crash** — pode duplicar um email já aceite pelo servidor.
2. **Guardar segredos SMTP na base de dados** — viola o modelo de segurança.
3. **Inferir destinatário e conteúdo livremente pela LLM** — não oferece uma fronteira verificável para uma ação externa.
