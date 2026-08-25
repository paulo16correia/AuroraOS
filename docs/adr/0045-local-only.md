# Design 0045 — Aurora is local only

**Status:** Decided by the owner · **Date:** 2026-08-25
**Supersedes:** `docs/adr/0039` (removed)
**Withdraws:** `docs/12-deployment.md` (removed)

## The decision

Aurora runs on the machine that owns its data, and nowhere else. There is no server deployment, no
container, no reverse proxy, no release pipeline. RFC 12 assumed a VPS; the owner does not want one,
and a normative document describing a shape the product will never take is worse than no document.

I built the RFC 12 work because it was on the list of unimplemented normative RFCs. That was
following the list rather than the product, and the list was wrong.

## Removed

`Dockerfile`, `.dockerignore`, `compose.yaml`, `ops/` (Caddyfile, release, rollback, backup and
restore scripts), `docs/12-deployment.md`, `docs/adr/0039-deployment.md`, and the
`DeploymentManifest` contract.

`Aurora:BindAddress` and `Aurora:AllowedHosts` are gone with them. They existed only so a container
could be reached from outside its own namespace. **Kestrel binds loopback, unconditionally**, and
the Host guard knows loopback and nothing else — which is a stronger position than the one they
replaced, not a weaker one. The security model rests on being unreachable rather than on a firewall
in front of something reachable.

## Kept, and why

Three things arrived in the same batch and are not about deployment. Removing them would have
broken working parts of the local product, which is not what was asked for:

**Health checks.** `ISelfModel` reads them: RFC 027's Self reports `DEGRADED` from observed health,
and the control panel shows it. "Is Aurora working" is a question on a laptop too. The endpoints
stay — `/health/live` reachable from loopback without a credential and carrying one word,
`/health` behind the guard with detail.

**The clock guard.** Approvals expire, consent sessions expire, signals expire. A clock that has
gone backwards turns every one of those into something else, and the server refuses to start on one
it does not trust. That is a local safety property; it was never about servers.

**Backup and restore-test.** RFC 12 asked for them, but a backup matters most on a single machine
with one copy of everything. `backup` and `restore-test` stay on the console, where they were —
producing one is a copy of everything Aurora knows, so it was never going to be an endpoint.

## Left alone deliberately

Some documents still contain the word. They mean different things and rewriting them would be
damage rather than tidying:

- `docs/041-world-model.md` uses a VPS as an **example of the owner's world** — "which VPS runs this
  service?" is a thing the world model is for, not a statement about where Aurora lives.
- `docs/040-domain-model.md` and `docs/08-learning.md` have `DEPLOYED` as a state of a
  `LearningProposal`. That is a lifecycle, not infrastructure.
- `docs/adr/0004` says "deployment" about **Azure OpenAI**, which is Azure's word for a model
  endpoint.
- Older ADRs mention deployment in passing. They are records of what was decided when, and editing
  them to match a later decision would make the record less true rather than more.
