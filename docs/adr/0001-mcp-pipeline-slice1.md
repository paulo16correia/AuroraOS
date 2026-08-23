# Design 0001 — Aurora MCP Pipeline, Slice 1

**Status:** Approved for It.0 · **Date:** 2026-07-21
**Relationship to the imported spec:** the RFCs, laws, governance and review documents under `docs/` are **non-binding reference** (owner's decision: "reference only"). This design knowingly **diverges** from Paulo's v1.0 freeze in favour of a minimal, iterable slice. The AuroraOS spec ADRs (ADR-000…029) that once sat beside these files were removed from this repository and remain in the `paulo16correia/AuroraOS` spec repo. The implementation's own design records live here, in `docs/adr/`.

## Objective

Implement the "Aurora Kernel" flow with a **local MCP entry point** (HTTP+SSE) and a surface of **two fixed tools**. Start minimal, iterate quickly, with offline tests.

## Pipeline (no Planner in this slice)

```
aurora_execute
  → Reasoner            (UNTRUSTED proposal: NL→{action_id,input}, or explicit passthrough)
  → Kernel select/validate (the KERNEL chooses and commits, not the LLM; the action must exist
                          in the catalog; input validates against JSON Schema; objective XOR
                          action_id; unknown fields rejected; size limits)
  → Policy              (fail-closed, evaluated WITH the input, immediately before the effect)
  → Consent             (LOW = automatic; ≥MEDIUM = DaVault-style session — from It.2 onwards)
  → Executor            (typed stub command)
  → Audit (hash chain) + Idempotency store
```

## MCP surface — 2 tools (dynamic catalog behind them)

**`aurora_catalog`** `{query?, detail?} -> {actions:[{action_id,title,description,input_schema,effects[],risk,approval_required}]}`

**`aurora_execute`** `{objective? XOR action_id+input, idempotency_key?} -> {status, resolved:{action_id,input,confidence,via}, consent?, result?, audit_ref[], error?}`
- Two modes: natural-language `objective` (the LLM resolves it, It.1+) OR explicit `action_id`+`input`. **Mutually exclusive.**

## Security invariants — from It.0 (cheap now, expensive to retrofit)

- **Transport:** Kestrel bound to **loopback only**, a mandatory **high-entropy local bearer token**, and `Host`/`Origin` checking (anti DNS-rebinding). The principal is the authenticated MCP client plus the local Windows user.
- **The reasoner only proposes:** the Kernel validates and commits the action; the LLM never authorises.
- **Fail-closed policy:** deny by default; re-evaluated with the `input` before the effect, since risk can depend on the input.
- **Hash-chained audit from It.0** (`previous_hash`/`record_hash`, SHA-256), append-only; an integrity failure is fail-closed. (RFC 09.)
- **Idempotency:** states `ACCEPTED|EXECUTING|COMPLETED|FAILED|UNKNOWN`; unique per (principal + `idempotency_key`); replaying a `COMPLETED` entry returns the stored result **and re-authenticates the caller**; the same key with a different `input` is a **conflict**.
- **SQLite:** WAL + `busy_timeout`, versioned schema (migrations), no secrets in the database.

## Consent Session (DaVault model) — It.2, done properly

`status → request → session → reuse`, `LOCKED`/`UNLOCKED`. One approval (a trusted desktop dialog plus passphrase) opens a **local, time-boxed, scoped** session that is reused without a fresh prompt. Mandatory corrections (Codex):

- `session_id` generated **server-side** and bound to principal, Windows session, server boot, policy version, risk ceiling and scope. **Never** accepted from the client.
- Prompts **serialised** (single-flight per key); bounded deadline plus SSE heartbeats; a client disconnect or cancel closes the prompt and **guarantees non-execution**.
- Policy, scope, expiry and revocation re-evaluated **atomically** before the effect.
- The prompt shows the **validated canonical action, the authenticated requester and the hash**, never client-supplied text (anti-spoofing); signed executable, foreground parented window, no secrets in logs.
- A real passphrase: KDF (Argon2/PBKDF2) plus verifier, throttling, enrollment and revocation — not `TaskDialog`, which has no text field; a dedicated dialog or the Windows Credential UI.
- Grants preferably held **in memory** (only the audit persists); invalidated on restart, logout, lock, user switch, policy change or clock failure. `LOW` is never covered by a session; `MEDIUM` never covers `HIGH/CRITICAL`.
- **Autonomy with effects (caveat):** a reused session that runs subsequent MEDIUM writes without prompting is, in practice, permanent autonomy with effects. DaVault does this to *read secrets*, not to *write*. Mandatory guards: a ceiling on action count and cost per session, a kill switch, per-action audit, and a reconsideration of whether the scope "everything ≤MEDIUM" should shrink to "this capability" for write effects.

## Architecture (C#, .NET 10, layered — no network or SDK in the domain)

```
src/Aurora.Core      contracts (records, snake_case) + pipeline + interfaces
                     (IReasoner, ICapabilityRegistry, ICapabilityExecutor,
                      IConsentSession, IApprovalPrompt, IAuditStore, IPolicyEngine)
src/Aurora.Adapters  AzureOpenAiReasoner + KeywordFallbackReasoner; SqliteAuditStore +
                     IdempotencyStore + ConsentSessionStore; DesktopConsentPrompt;
                     stub capabilities + registry
src/Aurora.Server    ASP.NET Core, MCP HTTP/SSE (2 tools),
                     loopback + bearer + Origin, DI; interactive desktop session
tests/Aurora.Tests   xUnit — unit (reasoner and prompt mocked) + integration (TestServer)
```

## Iterations

- **It.0 — secure skeleton (no LLM, no consent UI, no writes):** 2 tools; static catalog (`clock.now`, `echo.say` — LOW, read-only); `execute` with explicit `action_id` only; JSON Schema validation; fail-closed policy; hash-chained audit in SQLite; idempotency (states + replay + conflict); loopback + bearer + Origin. Tests green.
- **It.1 — reasoner:** Azure OpenAI (proposal) plus a keyword fallback **restricted to LOW**; the Kernel commits. Mocked in tests.
- **It.2 — Consent Session (DaVault):** everything in the section above, plus `files.write_sandbox` with path hardening (traversal, UNC/device, symlink/reparse, TOCTOU) and atomic writes.
- **It.3 — hardening:** `EXECUTING→UNKNOWN` recovery and reconciliation; metrics (active prompts, consent latency, idempotency conflicts, `UNKNOWN` executions, audit failures); an MCP interop test with a real client; backup/restore; **audit hardening** (truncation detection via an external head anchor plus HMAC signing with a key held outside SQLite — the unkeyed SHA-256 chain from It.0 detects a partial edit but neither truncation nor a wholesale rewrite by anyone with write access to the file); and **audit pre-image enrichment** (decision, reason, risk, `via`, `policy_ids`, which matters once the untrusted reasoner arrives in It.1).

## Tests

- **Unit (offline):** schema validation rejects invalid input, extra fields and oversized payloads; policy denies by default; idempotency (replay and conflict); (It.2) consent grants and denies, a session covers and expires a scope.
- **Integration:** `TestServer` — `catalog` plus LOW `execute` auto-granted; (It.2) MEDIUM open and reuse with a fake prompt; an SSE disconnect aborts.

## NuGet dependencies (names)

**Pinned versions with a supply-chain verdict BEFORE any `restore`; frozen `packages.lock.json` plus `--locked-mode`:**
`ModelContextProtocol` (+ `.AspNetCore`) · `Azure.AI.OpenAI` · `Microsoft.Data.Sqlite` · `JsonSchema.Net` · `xunit` + `xunit.runner.visualstudio` + `Microsoft.NET.Test.Sdk` + `Microsoft.AspNetCore.Mvc.Testing`.

## Codex plan review (2026-07-21) — adopted / deferred

**Adopted from It.0:** untrusted reasoner; fail-closed policy evaluated with the input; hash-chained audit; idempotency with states, conflict and re-auth on replay; loopback + bearer + Origin; XOR objective/action_id with strict fields and size limits; WAL/busy_timeout plus migrations; Planner removed.
**Deferred (It.2):** the consent and session corrections (server-side session binding, single-flight, deadline plus heartbeat, anti-spoofing, passphrase KDF) and sandbox hardening.
**Deferred (It.3):** UNKNOWN recovery and reconciliation, metrics, interop with a real MCP client, backup/restore.
**Accepted divergence:** we do not conform to Paulo's v1.0 freeze (Decision/CapabilityRequest/Action/Observation/Event Bus) — the "reference only" decision.
