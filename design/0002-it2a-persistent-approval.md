# Design 0002 — Persistent Approval (It.2, primeiro incremento)

**Estado:** Implementado · **Data:** 2026-07-24
**Depende de:** `design/0001-mcp-pipeline-slice1.md` (It.0)

## Objetivo

O It.0 deixou o `IConsentGate` a recusar sempre qualquer capability ≥MEDIUM
("sessões reais chegam em It.2"). Não havia caminho nenhum para uma ação
MEDIUM ser executada. Este slice implementa a primeira fatia real de It.2:
**aprovação persistida, ligada ao par exato (ação, input)**, mais a primeira
capability com efeito de estado real (`memory.remember` / `memory.recall`)
para a exercitar de ponta a ponta.

Não é a "Consent Session" completa descrita na secção "Consent Session
(modelo DaVault)" do design 0001 — essa continua em aberto (ver "Adiado"
abaixo). É um incremento mais pequeno, seguro e testável sem UI de
desktop nem passphrase, que substitui a recusa incondicional por um fluxo
pedir → decidir → consumir, um único uso por âmbito.

## Modelo de dados

```
Approval
  approval_id, principal_client_id, principal_windows_user
  action_id, scope_hash            -- scope_hash = requestHash existente do Kernel
                                       (Sha256(action_id + input canónico));
                                       qualquer alteração ao input muda o scope_hash.
  status: PENDING | APPROVED | REJECTED | CONSUMED
  created_at_utc, expires_at_utc, decided_at_utc
```

Uma única janela de 15 minutos cobre pedir-decidir-consumir: se a aprovação
não for decidida e consumida dentro desse prazo, expira e um novo pedido
para o mesmo âmbito cria um novo `PENDING`. Isto é uma simplificação
deliberada: o design 0001 distingue TTL de decisão vs. reutilização em
sessão; aqui há só um TTL, e a aprovação é de uso único (não fica em sessão
para cobrir pedidos futuros).

## Fluxo

```
aurora_execute(action_id=memory.remember, input={note})
  → Policy: MEDIUM + approval_required → ALLOW (antes: DENY sempre)
  → Consent: sem aprovação viva para este scope_hash → cria PENDING
  → resposta: status=denied, error.code=approval_required, consent.approval_id=<id>

aurora_approve(approval_id=<id>, decision=approved|rejected)
  → PENDING (do mesmo principal) → APPROVED|REJECTED, auditado

aurora_execute(action_id=memory.remember, input={note})   -- mesmo input exato
  → Consent: APPROVED vivo e não consumido → consome (one-time), grant
  → Executor grava a nota → completed
```

Uma rejeição é uma decisão humana deliberada: fica `REJECTED` e o mesmo
`scope_hash` continua denied (`consent_required`, terminal) até o conteúdo
mudar — não há novo pedido silencioso para o mesmo input.

## Interação com idempotência (correção necessária)

O Kernel já reservava a `idempotency_key` (estado `ACCEPTED`) antes de
avaliar consent. Antes deste slice, uma recusa de consent liquidava sempre
a reserva como `FAILED` terminal — inofensivo enquanto MEDIUM não tinha
caminho de aprovação. Agora seria um bloqueio real: o mesmo
`idempotency_key` reutilizado depois da aprovação replicaria a negação
antiga para sempre (`ReplayFailed`).

Correção: quando a decisão de consent é `requires_approval` (retomável),
o Kernel **abandona** a reserva em vez de a fechar como falha
(`IIdempotencyStore.AbandonAsync`, novo método — `DELETE ... WHERE state =
'ACCEPTED'`, compare-and-set). Uma nova tentativa com a mesma chave começa
uma reserva `Begin` do zero. Uma rejeição explícita continua a liquidar
como `FAILED` terminal, porque não é retomável para o mesmo input.

## Invariantes de segurança

- Fail-closed inalterado: só capabilities explicitamente marcadas
  `approval_required` ganham caminho de aprovação; tudo o resto ≥MEDIUM
  continua sempre negado.
- `scope_hash` liga a aprovação ao par exato ação+input (reaproveita o
  hash que o Kernel já calculava para idempotência) — mudar um campo
  invalida a aprovação, como no RFC 01 do `docs/` de referência.
- `aurora_approve` só decide um `PENDING` que pertença ao mesmo
  `principal_client_id`; nunca aceita o `approval_id` como prova de
  identidade.
- Toda a decisão (pedido, aprovação, rejeição) fica no audit log
  hash-chained existente.
- Uma aprovação nunca cobre uma ação diferente nem um input diferente —
  não há reutilização em sessão neste incremento.

## Adotado agora / Adiado

**Adotado:** aprovação persistida em SQLite; `aurora_approve` como terceira
tool MCP; `memory.remember` (MEDIUM, approval_required) e `memory.recall`
(LOW, leitura) como primeira capability com efeito real; correção de
idempotência para retomar depois de aprovação.

**Adiado (Consent Session completa, ver design 0001):** sessão
time-boxed reutilizável por múltiplas ações; `session_id` ligado a boot do
servidor + versão de política; diálogo de desktop dedicado com
passphrase (KDF, throttling, revogação); single-flight/serialização de
prompts; heartbeats SSE e aborto em disconnect; teto de nº de
ações/custo por sessão; kill-switch. Continua tudo por decidir pelo dono
do repositório antes de avançar para sessões reutilizáveis com escrita.
