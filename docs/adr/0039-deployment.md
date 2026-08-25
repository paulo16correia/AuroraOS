# Design 0039 — Deployment and continuity

**Status:** Implemented, with the container unbuilt here · **Date:** 2026-08-25
**Implements:** `docs/12-deployment.md`
**Advances:** M5 of `docs/13-roadmap.md`

## What a deployment has to answer

Not "does it run" — that is the easy half. RFC 12's rules are about the hard half: can this release
be reversed, can the backup be restored, does the system say when it is unwell, and does it refuse
to act when the ground under it has moved.

## Health, and why there are two endpoints

`GET /health/live` answers **`ok`** and nothing else, without a credential. A container runtime
polls it and holds no token; giving one to a health probe would be handing out a credential to save
a word. It can be open precisely because it says nothing.

`GET /health` carries the detail and stays behind the guard: six components — database, audit,
clock, event bus, scheduler, resources — each with `PASS|WARN|FAIL`, a latency, its dependencies and
a `detail_safe`. The field is named that way in the RFC and the name is the rule: a health endpoint
is the most-scraped surface a system has, so it carries counts and states and never content. A test
asserts no detail string contains a path.

Two design points that matter more than they look:

- **A failing check does not take the other five with it.** Each runs inside its own try, and a
  thrown exception becomes `FAIL` with the exception *type* — not its message, which can carry a
  path or a value. The moment a health answer matters most is when something is broken.
- **A schema mismatch is `FAIL`.** Rule 2 says a release passes checks before receiving traffic;
  serving against a schema the build does not expect is how a "reversible" migration becomes an
  incident.

`FAIL` answers 503, so a proxy can act on it without parsing anything. `WARN` still serves: it means
look, not stop.

## The clock, checked without a network

RFC 12's limit case says an incorrect clock blocks anything that depends on expiry. Approvals
expire, consent sessions expire, signals expire, schedules fire — every one is a promise about time,
and a clock that has gone backwards turns them into something else.

The check needs no NTP and no third party. **The audit log is append-only and monotonic**, so a
clock reading earlier than the newest audit record is a clock that has gone backwards, and time does
not do that. Thirty seconds of tolerance absorbs an NTP correction mid-write without absorbing the
thing being looked for, which is a jump of hours or years. The server refuses to start on a clock it
does not trust, and says that nothing needs repairing — only synchronising.

It deliberately does **not** claim to catch a clock that is uniformly fast or slow. That needs an
external reference, and claiming to detect it without one would be worse than admitting the limit.

## The image, and what it cannot do

Two stages, so the runtime carries no SDK and no source. An unprivileged fixed uid, a read-only root
filesystem, all capabilities dropped, `no-new-privileges`, and one volume that holds everything
durable. `dotnet restore --locked-mode`, so an image cannot quietly pick up a package nobody chose.

The process that holds the audit key, the vault key and the operator's memory does not own its own
binary. Aurora publishes no port at all: the proxy reaches it over an internal network, which is
what keeps its loopback assumption true on a machine with an internet-facing address.

## Release order is the design

Back up → build → start → **check health before traffic** → record the manifest → let the proxy
through. The manifest pins **digests, not tags**: a tag names a build, a digest is one. And
`rollback_release_id` is a required field rather than an optional one — a release with nowhere to go
back to is not reversible, and rule 2 asks for reversibility rather than for hope.

`ops/rollback.sh` deliberately does **not** restore data. A migration that already committed is not
undone by running an older binary, and pretending otherwise is how a rollback becomes the incident.
It says so, and points at the restore script.

## Backups that are proven, not assumed

`backup` writes a copy and verifies the audit chain **in the copy** — the moment to find out a
backup is worthless is now, not during a restore when the original may be gone. It is recorded as
`COMPLETE`, which is a weaker claim than it sounds.

`restore-test` restores into a temporary directory and re-verifies, and only then does the snapshot
become `RESTORE_TESTED`. Those are different claims and only the second is worth anything. The
isolated target is a temp directory rather than anything configured, so a restore test cannot land
on the instance it was meant to protect.

Both are console commands, not endpoints. A backup is a copy of everything Aurora knows, and an
endpoint that produced one on request would be a way to exfiltrate the instance with a credential
meant for something else.

**The keys are not in the backup**, and the scripts say so twice. A backup carrying its own signing
key proves nothing about itself.

## What I could not verify here

**The container was never built.** There is no Docker on this machine. What I did check: every path
the Dockerfile copies exists, `dotnet restore --locked-mode` and the exact `dotnet publish`
invocation both succeed, and all four shell scripts parse. What remains unproven is the image build
itself, the compose topology, and the healthcheck's shell one-liner. Whoever runs `docker compose
build` first is testing that for the first time, and should expect to.

**The proxy image is pinned by tag, not digest.** Rule 2 wants digests and `ops/release.sh` records
the ones it actually ran — but the value in `compose.yaml` is a tag, because resolving a digest
needs a registry this machine cannot reach. Pin it before a real deployment.

**TLS and DNS are configuration, not code.** The Caddyfile names `aurora.example.com` and obtains
its own certificate. Rule 1's controlled DNS and administrative access are the operator's, and
nothing here can do them on their behalf.
