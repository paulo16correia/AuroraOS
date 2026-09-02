# Completion baseline — 2026-09-02

An audit, not a feature. Every status below was checked against the code, the manifests, the
registrations and a test run — not against previous reports.

**Suite at the time of writing:** 1168 .NET tests, 0 failed, 0 skipped, 0 build warnings, tree
clean. 257 Python tests across three plugins, all run from the .NET suite.

## Status model

| | |
| --- | --- |
| IMPLEMENTED | Exists **and is wired into its intended runtime path**. |
| TESTED | Implemented, and exercised by deterministic tests against fakes or loopback. |
| VERIFIED | Actually executed against the real external provider or platform. |
| UNVERIFIED | Implemented and tested; real-world verification pending. |
| UNSUPPORTED | Intentionally not supported by architecture or provider constraint. |

## What the audit found

### 1. The Microsoft plugin could not be run by Aurora at all

Every Microsoft test ran the plugin's Python modules directly. That proves the code is right and
proves nothing about whether Aurora can start it. Running it through `ServicePluginHost` for the
first time, it failed in two ways:

- **Successes omitted `ok`.** The host reads `ok` from a result frame; a missing one is false, so
  every successful call was reported as `plugin_failed: no detail`.
- **Refusals used `kind: "error"`.** The host knows `ready`, `result` and `event`. An unknown kind
  is ignored by design, so every refusal hung until the 30-second timeout.

Neither was visible to 142 passing Python tests, because the harness asserted on the frames the
plugin wrote rather than the ones the host reads. **The harness agreed with the bug** — the same
failure shape as the invented executable path earlier in this repository's history.

Root cause of the blind spot: the plugin took the stand-in's address from an environment variable,
and both plugin hosts call `Environment.Clear()` before launching. Any test using that seam could
only ever run the plugin standalone. Fixed by moving to `config.json`, which is what the Discord
plugin already did.

**Fixed.** `MicrosoftRuntimeTests` now starts the real plugin through the real host: a round trip,
a Graph call through the host, a provider denial arriving as a refusal rather than a crash, an
unknown capability not killing the process, and no credential crossing the boundary.

### 2. Aurora refuses to start a plugin missing its declared secrets — and is right

The plugin has a "degraded start" branch that reports which credentials are missing.
`ServicePluginHost` never reaches it: a service plugin whose declared required secrets are absent
is not started, and the refusal names the missing one.

Aurora's behaviour is correct. "Start it anyway and let it explain" is how something ends up
running with half its configuration. The degraded branch is reachable **only when the plugin is run
standalone**, which is where its own tests run it. Recorded as a limitation rather than treated as
a feature. The audit's expectation was corrected, not the platform.

### 3. Voice has no runtime path

`SqliteVoiceSessionStore`, `VoicePolicyService` and `VoiceToolBridge` are implemented and tested,
and **none of them is registered in `ServiceRegistration`**. Nothing constructs them, no MCP tool
reaches them, no capability exposes them, and `voice_service.py` does not exist.

`DormantSurfaceTests` passed throughout, because the dead-contract law checks that an interface has
an implementation — not that anything is wired to it. That is a narrower guarantee than it reads
as, and worth knowing.

Voice is therefore **IMPLEMENTED at the domain and adapter level, TESTED at that level, and not
wired**. Not fixed here: the task says not to extend Voice, and wiring it is the Voice runtime
rather than a completion detail.

### 4. Eighteen tests nobody ran

`plugins/discord/test_conversation.py` passes and was referenced by nothing. A suite nobody
executes is documentation. **Fixed** — wired into `DiscordVoiceTests`.

### 5. A migration asserted on the wrong database

The v17 `voice_session` table was covered on a fresh database but not on an upgraded one, and those
are different code paths. **Fixed** — the v1→current migration test now asserts it.

## Inventory

### In-process capabilities — IMPLEMENTED, TESTED, registered

All seven have a real implementation, are registered in `ServiceRegistration`, and reach the Kernel
through `ICapabilityRegistry` → `AuroraKernel` → `ICapabilityExecutor`.

| Action | Status |
| --- | --- |
| `clock.now` | IMPLEMENTED · TESTED |
| `echo.say` | IMPLEMENTED · TESTED |
| `memory.remember` | IMPLEMENTED · TESTED |
| `memory.recall` | IMPLEMENTED · TESTED |
| `files.read_sandbox` | IMPLEMENTED · TESTED |
| `files.write_sandbox` | IMPLEMENTED · TESTED |
| `files.organise_sandbox` | IMPLEMENTED · TESTED |

### Cognitive subsystems — IMPLEMENTED, TESTED

Kernel, policy, approvals, consent sessions and named windows, hash-chained audit, idempotency and
reconciliation, event bus with outbox, vault, SQLite persistence with forward migrations, cognitive
cycle, planner, scheduler, memory, knowledge graph, world model, beliefs, relationships, missions,
curiosity, needs, signals, self model, personality, deliberation, life history, incidents, resource
model, mind state. All local; VERIFIED does not apply.

### Microsoft 365 — 54 capabilities

Manifest and handlers are coherent: 54 declared, 54 handlers, no orphans in either direction
(checked programmatically).

| Domain | Capabilities | Implementation | Registration | Authorization | Tests | Real tenant | Status |
| --- | ---: | --- | --- | --- | --- | --- | --- |
| Status/identity | 2 | yes | manifest | Kernel via bridge | 12 + 6 e2e | no | IMPLEMENTED · TESTED · UNVERIFIED |
| Mail | 10 | yes | manifest | Kernel via bridge | 20 | no | IMPLEMENTED · TESTED · UNVERIFIED |
| Calendar | 8 | yes | manifest | Kernel via bridge | 21 | no | IMPLEMENTED · TESTED · UNVERIFIED |
| Files (OneDrive + SharePoint) | 10 | yes | manifest | Kernel via bridge | 19 | no | IMPLEMENTED · TESTED · UNVERIFIED |
| To Do | 4 | yes | manifest | Kernel via bridge | 17 (with Planner) | no | IMPLEMENTED · TESTED · UNVERIFIED |
| Planner | 5 | yes | manifest | Kernel via bridge | (as above) | no | IMPLEMENTED · TESTED · UNVERIFIED |
| People/Directory | 5 | yes | manifest | Kernel via bridge | (as above) | no | IMPLEMENTED · TESTED · UNVERIFIED |
| Teams | 10 | yes | manifest | Kernel via bridge | 16 | no | IMPLEMENTED · TESTED · UNVERIFIED |

Shared foundation — authentication (refresh-token and client-credentials grants), Graph transport,
pagination with `@odata.nextLink` validation, bounded retries, `Retry-After`, structured Graph
errors, two-layer credential redaction, host allowlisting, credential-attached-last ordering,
identifier and OData injection hardening, untrusted-content bounding: **IMPLEMENTED · TESTED ·
UNVERIFIED** (37 tests).

**Not installed.** `plugin_installation` contains only `plugin/discord`. Installing is an owner
action with three consent prompts, not a code state — but it means no Microsoft capability has ever
appeared in a live catalogue.

### Discord — 35 capabilities

Manifest and handlers coherent (35/35). Gateway, guilds, channels, messages, reactions, threads,
presence and voice. **IMPLEMENTED · TESTED · VERIFIED on macOS** — gateway, voice join with
DAVE/MLS, listening and speaking all ran against real Discord. Linux and Windows UNVERIFIED.

### Windows — no regression

All five AppContainer files present, `PluginSandbox.ForThisMachine()` still selects it, the
create-suspended → verify-token → resume-or-terminate sequence intact, `OwnerOnly` intact. 22
tests. **IMPLEMENTED · TESTED · UNVERIFIED** — never executed on Windows.

### Voice

| Component | Status |
| --- | --- |
| `VoiceSession`, `VoiceGrant`, `OutboundCallIntent` | IMPLEMENTED · TESTED |
| `VoiceAuthorization` | IMPLEMENTED · TESTED (26) |
| `VoiceIdentity` | IMPLEMENTED · TESTED (17) |
| `VoicePolicyService` | IMPLEMENTED · TESTED (3) · **not registered** |
| `VoiceToolBridge` | IMPLEMENTED · TESTED (14, real Kernel) · **not registered** |
| `SqliteVoiceSessionStore` | IMPLEMENTED · TESTED (13) · **not registered** |
| Provider boundary, Realtime adapter, fakes | IMPLEMENTED · TESTED (34) |
| `voice_service.py` | **DOES NOT EXIST** |
| Runtime wiring / DI registration | **DOES NOT EXIST** |
| End-to-end audio | **DOES NOT EXIST** |

## Known limitations, classified

| Limitation | Kind | Note |
| --- | --- | --- |
| Graph file content download | **A — architectural** | Tenant-specific host; the manifest requires hosts by name. |
| Mail attachment content | **A — architectural** | Same host problem. |
| Teams transcript content | **A — architectural** | Same host problem. |
| Graph change notifications | **A — architectural** | Needs an endpoint Microsoft can reach; Aurora binds loopback. |
| Teams calls / real-time media | **A — architectural** | Needs a bot with a public signalling endpoint. |
| Inbound PSTN | **A — architectural** | Same; no listener ships. |
| Webhook ingress generally | **A — architectural** | ADR 0045. |
| Graph redirects | **C — deferred by design** | Never followed; a downloadUrl would send the token off-allowlist. |
| Permanent delete (files) | **C — deliberate** | Recycle bin only; `permanentDelete` not wired. |
| Creating a recurring series | **D — not implemented** | Recurrence patterns are shaped enough that half of one is wrong. |
| Teams recordings, channel creation | **D — not implemented** | — |
| Planner needs `Group.ReadWrite.All` | **B — provider** | No narrower delegated form exists. |
| Transcripts need app-only + admin | **B — provider** | `OnlineMeetingTranscript.Read.All` has no delegated form. |
| Script plugins on Windows | **D — not implemented** | `CreateProcess` will not run a `.py`; blocks any plugin there. |
| Voice runtime | **D — not implemented** | Foundation exists; nothing is wired. |

`@odata.nextLink` validation, host allowlisting, credential ordering and refresh-token handling are
all **implemented and tested**, not limitations.

## Completion matrix

| Area | Implemented | Tested | Verified | Status | Remaining |
| --- | :-: | :-: | :-: | --- | --- |
| Core capabilities (7) | yes | yes | n/a | complete | — |
| Cognitive subsystems | yes | yes | n/a | complete | — |
| Microsoft Mail | yes | yes | no | UNVERIFIED | real tenant |
| Microsoft Calendar | yes | yes | no | UNVERIFIED | real tenant |
| OneDrive | yes | yes | no | UNVERIFIED | real tenant; no content read |
| SharePoint | yes | yes | no | UNVERIFIED | real tenant; admin consent |
| To Do | yes | yes | no | UNVERIFIED | real tenant |
| Planner | yes | yes | no | UNVERIFIED | real tenant; wide permission |
| People | yes | yes | no | UNVERIFIED | real tenant |
| Teams | yes | yes | no | UNVERIFIED | real tenant; no calls/notifications |
| Microsoft runtime path | yes | yes | no | fixed this audit | install on a live Aurora |
| Discord | yes | yes | **yes** (macOS) | VERIFIED | Linux/Windows runs |
| Windows sandbox | yes | yes | no | UNVERIFIED | a Windows machine |
| Windows key protection | yes | yes | no | UNVERIFIED | a Windows machine |
| Voice foundation | yes | yes | n/a | IMPLEMENTED · TESTED | — |
| Voice runtime | **no** | no | no | **unfinished** | service, DI, audio |
| Twilio | partial (boundary) | yes (fakes) | no | UNVERIFIED | account, number |
| OpenAI Realtime | partial (adapter) | yes (fakes) | no | UNVERIFIED | API key, session |
| +351 phone | no | no | no | unfinished | number, ingress |
| Real Microsoft Graph | n/a | n/a | no | UNVERIFIED | tenant |

## Summary

**Genuinely complete.** The seven in-process capabilities and every cognitive subsystem. Discord,
which is the only integration verified against its real service.

**Implemented and tested but unverified.** All 54 Microsoft capabilities and their shared
foundation; the Windows AppContainer and key protection; the voice foundation; the Twilio and
Realtime adapters.

**Intentionally unsupported.** Anything needing an endpoint a provider can reach — Graph change
notifications, Teams calls, inbound PSTN — and anything served from a host a manifest cannot name:
file content, attachment content, transcript content. All architectural, all consequences of
Aurora's own local-only property rather than provider gaps.

**Actually unfinished.** The Voice runtime: no `voice_service.py`, no DI registration, no audio
path. Recurring-series creation, Teams recordings and channel creation. Script plugins on Windows.

**Highest-priority next task.** The Voice runtime — the service program and its DI registration —
because it is the only area where substantial implemented, tested code has no path to execution at
all. Everything else in this repository either runs or is honestly blocked on hardware or
credentials that do not exist yet.
