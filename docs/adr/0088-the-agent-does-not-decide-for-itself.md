# Design 0088 — The agent does not decide for itself

**Status:** Implemented · **Date:** 2026-10-02
**Supersedes:** the opt-in guard in `docs/adr/0011`, and the bearer guard on `/metrics` in
`docs/adr/0008`.
**Rests on:** `docs/adr/0011` (operator passphrase), RFC 11 (the panel's operator session)

## Context

An approval is a person deciding. Aurora has two surfaces where one can be decided:

- **`aurora_approve`**, an MCP tool. The MCP client is the agent, so on this surface the only thing
  that can tell a person from the agent is a secret the agent does not hold — the operator
  passphrase of `docs/adr/0011`.
- **The control panel**, behind an operator session minted on the server's console. The session is
  itself a credential the agent never holds.

`docs/adr/0011` made the passphrase optional, so that installations without one kept working. On
such an installation the tool's surface has nothing to tell the two apart. The same reasoning applies
to `/metrics`: `docs/adr/0008` kept it off MCP so that the agent does not read how often it is
refused, and the bearer token is the agent's credential.

## Decision

**`aurora_approve` decides only with the operator passphrase.** With none enrolled it answers
`passphrase_not_enrolled`, for a rejection as well as an approval — burying a request a person wanted
is still deciding it — and says how to proceed: decide in the panel, or run `enroll-passphrase`.

**The panel decides with or without one.** The Kernel takes which surface a decision came from as a
parameter, set only by the panel's endpoint after its operator check. With a passphrase enrolled,
both surfaces require it.

**The panel lists what is waiting.** `GET /v1/approvals`, operator-only, returns each pending
request with its action, risk, effects and the exact input it would run with. The input is kept on
the approval only while it is pending and is cleared when the approval is decided or expires.

**`/metrics` requires an operator session.**

A missing or deleted verifier file therefore leaves the tool's surface refusing, not deciding.

## Consequences

Deciding an approval needs one of two things a person does: open the panel (`ui` on the console), or
enrol a passphrase once. The integration tests decide through the panel, as a person would.

## Limits

The trust boundary is still the process user, as `docs/reference/audit-and-vault-threat-model.md`
sets out. An MCP client that also has a shell as the same OS user can read the key files and the
database and can reach the server's console; this decision governs what the agent's *tool* may do,
not what an agent holding the owner's account may do.
