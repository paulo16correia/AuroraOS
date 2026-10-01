# Design 0079 — Operational hardening for a controlled demo

**Status:** Implemented · **Date:** 2026-09-07
**Rests on:** `docs/adr/0072`/`0078` (Windows confinement), `docs/adr/0005` (audit key),
`docs/adr/0014` (vault key), `docs/adr/0036` (owner-only), `docs/adr/0067`/`0075` (service plugins)

## Why now

The readiness audit found the core sound and the gaps operational: a deployer could not tell why a
plugin was refused, a key's owner-only protection could silently fail to apply, a misbehaving plugin
could make the host buffer without bound, and there was no single deployment guide. This is the
small hardening sprint that closes those four, and nothing else. No capability, kernel, policy,
approval, or confinement behaviour changed.

## F-2 — `aurora doctor`

A new console preflight, separate from `health` (which asks a *running* instance whether it is
live). `doctor` runs against the files and configuration on disk and reports PASS/WARN/FAIL/INFO
for: the database path's writability, every key file's presence and **actual** owner-only state,
the sandbox root, the confinement mechanism, each installed plugin's manifest/interpreter/
interpreter-directory-grantability, and each required secret's presence. It exits non-zero on any
FAIL so it can gate a scripted start.

The checks live in `Aurora.Adapters.Diagnostics.Preflight`, pure of the console, so they are
unit-tested without a server or a Windows kernel; `DoctorConsole` renders them and wires the real
collaborators the same offline way `PluginConsole` already does. It reads state and never changes
it — a diagnostic that repaired what it found would hide the problem — and it never prints a secret
value, only names, paths and outcomes. Interpreter-directory grantability is checked read-only
(ownership and ACE inspection), which is the Windows blocker from `docs/adr/0078` surfaced before
first call rather than as a `SERVICE_UNAVAILABLE` at it.

## F-3 — owner-only key protection is fail-closed

`OwnerOnly.File`/`Directory` already reported whether they succeeded; the callers ignored it, so a
key could be written and then read back from a file behind a permission Aurora had failed to narrow.
Now:

- `OwnerOnly.Write` throws `OwnerOnlyProtectionException` if the restriction cannot be established —
  after removing the still-empty placeholder, so no secret is ever written to an unprotected file.
  Its three callers all write security-critical material (the symmetric keys via `LocalKeyFile`, the
  genome private key, the operator passphrase verifier), so this covers creation universally.
- `LocalKeyFile` and the genome signer re-establish owner-only on the **reload** path via
  `OwnerOnly.Require`, and refuse if it cannot be done — so a key from an earlier run, or one whose
  ACL drifted, is never read back from a file that is no longer the owner's alone.

There is no permissive fallback and no DPAPI/keystore (that remains future work): the answer to a
volume that will not restrict a file is to put the data where it can be restricted — a per-user
location — not to lower the bar. The sandbox *root* stays advisory as before (it is not key
material, and the reader/writer keep their lexical and link defences); F-3 is scoped to key
material.

## F-4 — the plugin host bounds its input

The service protocol is line-delimited JSON and nothing on the wire bounded a line, so a plugin
emitting one enormous line would make the host buffer all of it. `BoundedLineReader` now reads
through a fixed scratch buffer and refuses any line past `AuroraLimits.MaxPluginFrameChars`
(1 MiB), discarding the remainder to the next newline so the stream stays framed and the service
stays usable — the oversized line is dropped before it is ever fully held. A second, tighter bound,
`AuroraLimits.MaxObservationPayloadBytes` (256 KiB), drops an over-large observation payload rather
than publishing it to the event bus. Both are recorded on the service's state so a plugin that does
this is visible, and neither is a reason to drop the connection.

The limits reuse `AuroraLimits` rather than inventing a configuration surface. 1 MiB comfortably
holds a large legitimate result (pages of mail, a long message history) while capping any one
frame; 256 KiB reflects that an observation is a notification, not a bulk transfer. A frame at
exactly the limit is accepted; the first character past it drops the frame. Nothing truncates JSON
into invalid JSON — an over-limit frame is refused whole.

## F-5 — the operator runbook

`docs/guides/operator-runbook.md`, Windows-first and practical enough to follow without the RFCs:
prerequisites (including why a system-wide Python may fail AppContainer ACL granting for a
non-administrator), installation and directory layout, configuration, the security prerequisites,
secret provisioning, the plugin install-then-restart flow, connecting an MCP client, `aurora
doctor`, demo preparation, an honest IMPLEMENTED/TESTED/VERIFIED/UNVERIFIED/UNSUPPORTED status for
Microsoft/Discord/Voice, troubleshooting, and a controlled-demo checklist. It contains no real
credentials.

## What did not change

No new capability, no change to the kernel authority path, policy, approvals, or the fail-closed
posture; no shell or arbitrary process launch introduced; AppContainer verification and
`LocalOnlyTests` untouched; no test skipped, deleted, or weakened. The control plane remains
loopback-only.
