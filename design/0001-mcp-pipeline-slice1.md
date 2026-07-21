# Design 0001 — Aurora MCP Pipeline, Slice 1

**Estado:** Aprovado para It.0 · **Data:** 2026-07-21
**Relação com `docs/`:** os docs importados (RFCs, `docs/adr/ADR-006…013`, `docs/governance/architecture-freeze-v1.0.md`) são **referência não-vinculativa** (decisão do dono: "só referência"). Este design **diverge** conscientemente do freeze v1.0 do Paulo, favorecendo um slice mínimo e iterável. Documentos próprios do NCode vivem em `design/`, separados de `docs/`.

## Objetivo

Implementar o fluxo "Aurora Kernel", mas com **entrada MCP local** (HTTP+SSE) e superfície de **2 tools fixas**. Começar mínimo, iterar rápido, com testes offline.

## Pipeline (sem Planner neste slice)

```
aurora_execute
  → Reasoner            (proposta NÃO-CONFIÁVEL: NL→{action_id,input} ou passthrough explícito)
  → Kernel select/valida (o KERNEL escolhe/committa, não o LLM; ação existe no catálogo;
                          input valida contra JSON Schema; objective XOR action_id;
                          rejeita campos desconhecidos; limites de tamanho)
  → Policy              (fail-closed, avaliada COM o input, imediatamente antes do efeito)
  → Consent             (LOW = auto; ≥MEDIUM = sessão estilo DaVault — só a partir de It.2)
  → Executor            (comando stub tipado)
  → Audit (hash-chain) + Idempotency store
```

## Superfície MCP — 2 tools (catálogo dinâmico por trás)

**`aurora_catalog`** `{query?, detail?} -> {actions:[{action_id,title,description,input_schema,effects[],risk,approval_required}]}`

**`aurora_execute`** `{objective? XOR action_id+input, idempotency_key?} -> {status, resolved:{action_id,input,confidence,via}, consent?, result?, audit_ref[], error?}`
- Dois modos: `objective` NL (LLM resolve, It.1+) OU `action_id`+`input` explícito. **Mutuamente exclusivos.**

## Invariantes de segurança — desde a It.0 (baratos, caros de retrofitar)

- **Transporte:** Kestrel **só em loopback** + **bearer local de alta entropia** obrigatório + verificação de `Host`/`Origin` (anti DNS-rebinding). Principal = cliente MCP autenticado + utilizador Windows local.
- **Reasoner é proposta:** o Kernel valida e committa a ação; o LLM nunca autoriza.
- **Policy fail-closed:** nega por defeito; reavaliada com o `input` antes do efeito. Risco pode depender do input.
- **Auditoria encadeada por hash já na It.0** (`previous_hash`/`record_hash`, SHA-256), append-only; falha de integridade = fail-closed. (RFC 09.)
- **Idempotência:** estados `ACCEPTED|EXECUTING|COMPLETED|FAILED|UNKNOWN`; unicidade por (principal + `idempotency_key`); replay de `COMPLETED` devolve resultado guardado **e re-autentica o caller**; mesma key com `input` diferente = **conflito**.
- **SQLite:** WAL + `busy_timeout`, schema versionado (migrations), sem segredos na BD.

## Consent Session (modelo DaVault) — It.2, feito a sério

`status → request → session → reuse`, `LOCKED`/`UNLOCKED`. Uma aprovação (diálogo desktop confiável + passphrase) abre sessão **local, time-boxed, com âmbito**; reutiliza-se sem novo prompt. Correções obrigatórias (Codex):
- `session_id` gerado **server-side**, ligado a principal + sessão Windows + boot do servidor + versão de política + teto de risco + âmbito. **Nunca** aceite do cliente.
- Prompts **serializados** (single-flight por key); deadline limitado + heartbeats SSE; disconnect/cancel do cliente → fecha o prompt e **garante não-execução**.
- Reavaliar política/âmbito/expiração/revogação **atomicamente** antes do efeito.
- O prompt mostra a **ação canónica validada + requester autenticado + hash**, não texto do cliente (anti-spoofing); executável assinado, janela foreground parented, sem segredos em logs.
- Passphrase real: KDF (Argon2/PBKDF2) + verifier + throttling + enrollment + revogação — não `TaskDialog` (não tem campo de texto); diálogo próprio ou Windows Credential UI.
- Concessões preferencialmente **em memória** (só auditoria persiste); invalidar em restart/logout/lock/troca de utilizador/mudança de política/falha de relógio. `LOW` nunca coberto por sessão; `MEDIUM` nunca cobre `HIGH/CRITICAL`.
- **Autonomia com efeito (caveat):** uma sessão reutilizada que corre escritas MEDIUM seguintes sem prompt é, de facto, autonomia permanente com efeito (o DaVault faz isto para *ler segredos*, não para *escrever*). Guardas obrigatórias: teto de nº de ações/custo por sessão, kill-switch/pausa, auditoria por ação, e reponderar se o âmbito `todas ≤MEDIUM` não deve ser reduzido a `esta capacidade` para efeitos de escrita.

## Arquitetura (C#, .NET 10, por camada — sem rede/SDK no domínio)

```
src/Aurora.Core      contratos (records, snake_case) + pipeline + interfaces
                     (IReasoner, ICapabilityRegistry, ICapabilityExecutor,
                      IConsentSession, IApprovalPrompt, IAuditStore, IPolicyEngine)
src/Aurora.Adapters  AzureOpenAiReasoner + KeywordFallbackReasoner; SqliteAuditStore +
                     IdempotencyStore + ConsentSessionStore; DesktopConsentPrompt;
                     capacidades stub + registo
src/Aurora.Server    ASP.NET Core (net10.0-windows), MCP HTTP/SSE (2 tools),
                     loopback+bearer+Origin, DI; sessão desktop interativa
tests/Aurora.Tests   xUnit — unit (reasoner+prompt mockados) + integração (TestServer)
```

## Iterações

- **It.0 — esqueleto seguro (sem LLM, sem consent UI, sem escrita):** 2 tools; catálogo estático (`clock.now`, `echo.say` — LOW read-only); `execute` só com `action_id` explícito; validação JSON Schema; policy fail-closed; audit hash-chain em SQLite; idempotência (estados + replay + conflito); loopback+bearer+Origin. Testes verdes.
- **It.1 — reasoner:** Azure OpenAI (proposta) + fallback de palavras-chave **restrito a LOW**; Kernel committa. Mockado nos testes.
- **It.2 — Consent Session (DaVault):** tudo o da secção acima + `files.write_sandbox` com hardening de caminho (traversal, UNC/device, symlink/reparse, TOCTOU) e escrita atómica.
- **It.3 — endurecer:** recuperação `EXECUTING→UNKNOWN`/reconciliação, métricas (prompts ativos, latência de consentimento, conflitos de idempotência, execuções `UNKNOWN`, falhas de audit), teste de interop MCP com cliente real, backup/restore, **endurecimento da auditoria** (deteção de truncagem via cabeça-âncora externa + assinatura HMAC com chave fora do SQLite — a cadeia SHA-256 não-keyed de It.0 só deteta edição parcial, não truncagem nem reescrita total por quem tenha acesso de escrita ao ficheiro) e **enriquecimento do pre-image de auditoria** (decisão/motivo/risco/`via`/`policy_ids`, útil quando o reasoner não-confiável entrar em It.1).

## Testes

- **Unit (offline):** validação de schema rejeita input inválido/campos extra/oversized; policy nega por defeito; idempotência (replay/conflito); (It.2) consentimento autoriza/nega, sessão cobre/expira âmbito.
- **Integração:** `TestServer` — `catalog` + `execute` LOW auto; (It.2) MEDIUM open/reuse com prompt fake; disconnect SSE aborta.

## Dependências NuGet (nomes)

**Versões fixadas + verdict supply-chain ANTES de qualquer `restore`; frozen `packages.lock.json` + `--locked-mode`:**
`ModelContextProtocol` (+ `.AspNetCore`) · `Azure.AI.OpenAI` · `Microsoft.Data.Sqlite` · `JsonSchema.Net` · `xunit` + `xunit.runner.visualstudio` + `Microsoft.NET.Test.Sdk` + `Microsoft.AspNetCore.Mvc.Testing`.

## Codex plan-review (2026-07-21) — o que foi adotado / adiado

**Adotado desde It.0:** reasoner não-confiável; policy fail-closed com input; audit hash-chain; idempotência com estados + conflito + re-auth no replay; loopback+bearer+Origin; XOR objective/action_id + strict fields + size limits; WAL/busy_timeout + migrations; Planner removido.
**Adiado (It.2):** correções de consentimento/sessão (server-side session binding, single-flight, deadline+heartbeat, anti-spoofing, KDF de passphrase), sandbox hardening.
**Adiado (It.3):** recuperação UNKNOWN/reconciliação, métricas, interop com cliente MCP real, backup/restore.
**Divergência assumida:** não conformamos ao freeze v1.0 do Paulo (Decision/CapabilityRequest/Action/Observation/Event Bus) — decisão "só referência".
