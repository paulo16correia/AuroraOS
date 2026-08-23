# Design 0011 — Operator passphrase on approval (It.2, follow-up)

**Status:** Implemented · **Date:** 2026-08-23
**Depends on:** `docs/adr/0002-it2a-persistent-approval.md`, `docs/adr/0010-it2c-consent-sessions.md`

## The weakness

`aurora_approve` is an MCP tool. The MCP client is the agent. So until now the
agent could approve its own request.

Every layer built so far — fail-closed policy, an untrusted reasoner that only
proposes, approvals bound to the exact input, sessions that never cover writes —
rests on the assumption that *a human decides*. Nothing enforced that. The
approval gate looked rigorous and was, against everything except the one party it
existed to constrain.

Design 0001 assigned this job to a trusted desktop dialog with a passphrase. The
window is one half of that; the secret is the other. **The secret half works with
no window at all**, and it is the half that actually distinguishes a human from
the agent, so it is built first.

## What changed

When a passphrase is enrolled, `aurora_approve` requires it. The agent can still
call the tool; it cannot supply the secret.

Three details that matter more than they look:

- **Rejection needs the passphrase too.** Otherwise an agent could bury a request
  a human was about to approve — denial of the human's intent is still control
  over the outcome.
- **The check runs before the decision is applied**, so a refused attempt leaves
  the approval untouched and still pending.
- **Deployments with no passphrase enrolled behave exactly as before.** The guard
  is opt-in, because forcing it would break every existing local setup with no
  migration path. That is a real weakening and it is the operator's choice to
  make, not a default we quietly picked for them.

## Crypto and storage

**PBKDF2-HMAC-SHA256, 600k iterations, 16-byte salt, 32-byte verifier.** Argon2 is
the better KDF and is not used, for the same reason the Azure SDK is not used:
design 0001 requires a supply-chain verdict before any package is added, and
PBKDF2 is in the BCL. Argon2 is deferred, not dismissed.

Comparison is `CryptographicOperations.FixedTimeEquals`.

**The verifier lives in its own owner-only file, not in SQLite**, mirroring the
audit key decision in `docs/adr/0005-it3a-audit-hardening.md`. An attacker with
write access to the database could otherwise replace the verifier with a hash of
a passphrase they know, and then approve whatever they liked.

**Throttling:** five consecutive failures trigger a lockout that backs off
exponentially to a fifteen-minute cap. While locked, the correct passphrase is
refused too, and refused *without* running the KDF — throttling that still does
the work is not throttling, and accepting a correct guess during a lockout would
make the lockout advisory.

**A corrupt verifier file fails closed.** It is treated as enrolled-but-unusable
rather than not-enrolled, because the one outcome a damaged file must never
produce is a silently disabled guard.

## Enrolment is on the console, not over HTTP

`dotnet run -- enroll-passphrase` and `revoke-passphrase`, reading without echo
from the server host's console.

Not an HTTP endpoint and not an MCP tool. The bearer token belongs to the MCP
client, so anything reachable with it is reachable by the agent — and an agent
that can enrol its own passphrase can approve its own requests, which is the
exact hole being closed. The host console is the one channel the agent does not
have.

Enrolment refuses to overwrite an existing passphrase; replacing one is an
explicit revoke followed by a fresh enrolment.

## What is still missing

The trusted window itself: a signed executable, a foreground-parented dialog, and
prompt content the client cannot spoof. Those need a desktop toolkit on a
platform this work could not build or verify on, and they remain the open item
from `docs/adr/0010-it2c-consent-sessions.md`.

What the passphrase does *not* prove: that a human, rather than a script holding
the secret, made the decision. It raises the bar from "the agent can approve
itself" to "the agent must obtain a secret it was never given". That is a large
step and not the whole journey.

## A test-infrastructure defect fixed along the way

`SqliteTestDb.Dispose` calls `SqliteConnection.ClearAllPools()`, which is
process-wide. Under xUnit's default parallelism, one test finishing could drop
pooled connections belonging to an unrelated test mid-operation, and the symptom
was sporadic failures in the audit and backup tests, which had nothing to do with
the cause. Test classes now run sequentially; the suite still finishes in under a
second. Verified over six consecutive runs.

## Tests

19 new tests. Authenticator: not enrolled; enrol and verify; a missing or empty
candidate rejected rather than accepted; overwrite refused; short passphrase
refused; revoke; plaintext never stored; the salt making two enrolments of the
same passphrase differ; lockout engaging; lockout refusing the correct
passphrase; lockout expiring; a success clearing the failure count; and a corrupt
file failing closed.

Kernel: approving without the passphrase refused **and the approval left
undecided**; wrong passphrase refused; correct passphrase decides; rejection also
guarded; lockout refused; and no enrolment leaving the old behaviour intact.

Integration over real MCP: the agent calls `aurora_approve` and is refused, the
request is confirmed still denied, and the same call with the operator's secret
goes through.

## Deferred

Argon2 pending a supply-chain verdict; the trusted desktop window; per-approval
re-prompting rather than one secret for the session; and hardware-backed
verification (TPM, Secure Enclave), which would move the guard out of reach of a
filesystem attacker entirely.
