# Design 0010 — Consent sessions, read-only (It.2, final increment)

**Status:** Implemented · **Date:** 2026-08-23
**Depends on:** `docs/adr/0002-it2a-persistent-approval.md`, `docs/adr/0003-it2b-sandbox-write.md`
**Closes:** It.2

## The decision that shapes this

Design 0001 described a reusable, time-boxed session and then, in the same
breath, flagged the danger:

> a reused session that runs subsequent MEDIUM writes without a prompt is, in
> practice, permanent autonomy with effects. DaVault does this to *read secrets*,
> not to *write*.

The repository owner decided: **reuse covers reads only.** A write always costs a
fresh approval, bound to its exact input.

That asymmetry is the whole design, and it is defensible rather than merely
cautious. Repeating a read changes nothing, so amortising one human decision
across several reads grants no authority the user did not already give. Repeating
a write changes the world each time, so a single approval cannot honestly stand
in for the second one.

## What a session is

```
ConsentSession
  session_id, principal_client_id, principal_windows_user
  server_boot_id, policy_version
  status: ACTIVE | REVOKED
  actions_used, max_actions
  created_at_utc, expires_at_utc
```

Eligibility is checked by the gate, not the store: a capability qualifies only
when it declares **no effects** and sits at **MEDIUM or below**. HIGH and
CRITICAL are excluded even when read-only, because what usually makes them HIGH
is the sensitivity of what they read.

## Liveness is a WHERE clause, not a job

`server_boot_id` and `policy_version` are part of a session's identity. A restart
produces a new boot id and a policy change a new version, so every existing
session simply stops matching. Expiry and the action budget are in the same
predicate.

There is no sweeper and no background job. A session that stops matching is dead
by construction — there is nothing to forget to run, and no window in which a
stale grant is still usable. `TryUseAsync` spends one unit of budget in the same
statement that selects the session, so concurrency cannot overspend it.

## Kill switch

`POST /sessions/revoke`, behind the same loopback and bearer guard as the rest of
the operator surface, revokes every active session.

Deliberately **not** scoped to the current boot: an operator hitting the kill
switch should not have to reason about restarts. And deliberately not an MCP
tool — the agent should not be able to reason about its own leash.

## `files.read_sandbox`

Session reuse needs a read worth approving at least once, so this increment adds
the read counterpart to `files.write_sandbox` (MEDIUM, `approval_required`, no
effects), which design 0003 had listed as deferred.

It applies the same two path defences as the writer — lexical validation, then a
refusal to follow any link between root and target. A read is not harmless: a
planted symlink would exfiltrate a file from outside the sandbox as effectively
as a write would corrupt one. The shared checks moved into `SandboxGuard` rather
than being copied.

## The desktop dialog is not built

Design 0001 specifies a trusted desktop dialog with a passphrase (KDF,
throttling, enrollment, revocation) as the way a session is opened. **That is not
implemented.** The repository targets `net10.0` cross-platform since commit
`67591c4`, and a Windows desktop consent dialog could not be built or verified in
the environment this work was done in.

What exists instead: a session is opened by the existing `aurora_approve` flow —
a human approving one read. The security properties that do not depend on the UI
(server-side session identity, boot and policy binding, expiry, action ceiling,
kill switch, read-only scope) are all in place; the ones that do — a trusted
window, anti-spoofing of the prompt content, a real passphrase — are not.

The passphrase half of that dialog was built separately in
`docs/adr/0011-it2d-operator-passphrase.md`, which closes the part that actually
distinguishes a human from the agent. The trusted window itself remains the
open item, and should be closed before Aurora runs anywhere the local user is
not already fully trusted.

## Removed

`PersistentApprovalConsentGate` is deleted rather than left beside its
replacement. Two consent gates in one codebase is an invitation to wire the
weaker one by mistake. Tests that exercise the approval path alone now use
`SessionAwareConsentGate` with a session store that never covers anything, which
reproduces the old behaviour exactly.

## Tests

21 new tests. Store: no session, open-then-use, reuse instead of stacking
budgets, budget exhaustion, expiry, **restart invalidation**, **policy-change
invalidation**, another principal not covered, the kill switch reaching earlier
boots, and 20 concurrent uses never overspending a budget of 5.

Gate: LOW still auto-grants; approving a read opens a session that covers a
*different* read; **a live session never covers a write**; approving a write
opens no session; each write needs its own approval; a HIGH read is never
covered; revoking sends reads back to approval.

Integration, over real MCP: approve one read then read a second, never-approved
file with `consent.via = session`; a write still refused while a session is live;
the kill switch requiring the bearer token and sending reads back to approval.

## Deferred

The desktop passphrase dialog and everything that depends on it; SSE heartbeats
and abort-on-disconnect; per-session cost accounting as opposed to a plain action
count; and per-capability scoping narrower than "read-only ≤MEDIUM".
