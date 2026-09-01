# Architecture map — read before extending Aurora

**Date:** 2026-09-01 · **Purpose:** Phase 1 of the enterprise/Windows programme. Written by
reading the repository, not by reading its documentation. Where the two disagree, the code wins
and the disagreement is recorded below.

## Shape

| Project | Files | Lines | What it owns |
| --- | ---: | ---: | --- |
| `Aurora.Core` | 115 | 10 597 | Contracts, abstractions, Kernel, cryptography. No I/O. |
| `Aurora.Adapters` | 102 | 23 308 | Every implementation: SQLite, plugins, policy, cognition. |
| `Aurora.Server` | 17 | 3 487 | Hosting, MCP surface, REST, console commands, panel. |
| `Aurora.Tests` | 82 | 24 118 | 1 040 tests, currently green. |

The dependency direction is `Server → Adapters → Core`, and Core references nothing of the other
two. That is the seam every extension has to respect.

## The execution path, as the code actually runs it

`AuroraTools` (MCP) → `AuroraKernel.ExecuteAsync` → `ResolveAsync` → `AuthorizeAsync` →
`CommitAsync` → `ICapabilityExecutor` → `ICapability` → effect → audit → response.

Authorization is eight numbered steps inside `AuroraKernel.AuthorizeAsync`, ending at consent.
`SessionAwareConsentGate` decides between auto-grant, a live session, a named window
(docs/adr/0070) and a one-time approval. **Nothing reaches an effect except through this path.**

## Two kinds of capability, and the difference matters

| | In-process (`ICapability`) | Plugin (`plugin.json` + subprocess) |
| --- | --- | --- |
| Written in | C#, inside `Aurora.Adapters` | Any language, own process |
| Example | `files.read_sandbox` | `plugin/discord` (35 capabilities) |
| Confinement | none needed — it *is* Aurora | `sandbox-exec` / bubblewrap / **nothing on Windows** |
| Network | **no seam exists** (see below) | the sandbox's network grant |
| Runs on Windows | **yes** | **no** — refused, by design |

Both arrive in the same catalogue through `ICapabilityRegistry`, and both are governed identically
by the Kernel. `PluginCapabilityBridge` is what makes a plugin's manifest entry into an
`ICapability`, so a plugin capability is not a second class of thing — it is the same contract with
a subprocess behind it.

## Findings

### F1 — Plugin execution is unsupported on Windows, by deliberate refusal

`PluginSandbox.ForThisMachine()` returns `UnconfinedSandbox` on Windows, and both hosts
(`SubprocessPluginHost:81`, `ServicePluginHost:257`) refuse to launch when the plan is not
`Confined` unless `Aurora:Plugins:AllowUnconfined` is set. The default is `false`.

This is correct behaviour and should not be weakened. Its consequence is the single most important
constraint on this programme: **anything shipped as a plugin does not exist on Windows.**

### F2 — The sandbox seam cannot express AppContainer

`IPluginSandbox.Plan()` returns a `SandboxPlan(FileName, Arguments, …)` — it rewrites a command
line, because that is what `sandbox-exec` and `bwrap` are: wrapper programs. Windows confinement is
not a wrapper. AppContainer needs `CreateProcessAsUser` with
`PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES`, which `ProcessStartInfo` does not reach.

The abstraction is Unix-shaped. Supporting Windows means the seam becomes *start this process
confined* rather than *rewrite this command*. This is the case section 2.1(5) contemplates.

### F3 — Key material is unprotected on Windows

`LocalKeyFile:54`, `Pbkdf2PassphraseAuthenticator:205`, `EcdsaGenomeSigner:56` all set
`UnixCreateMode = UserRead | UserWrite`, and all skip it on Windows. `SandboxGuard.RestrictToOwner`
documents itself as a no-op there. So on Windows the audit key, the vault key, the genome key and
the passphrase verifier are created with whatever the parent directory's ACL grants.

A real security defect on the platform being promoted to first-class — section 2.1(2).

### F4 — Aurora has no outbound HTTP seam

`HttpClient` appears exactly once in `src/`, in `OperationsConsole.cs`. Every external call Aurora
makes today happens *inside a sandboxed plugin*, where the sandbox's network grant is the boundary
and the plugin owns its own retries and rate limits.

An in-process Microsoft Graph capability has no such boundary. Nothing exists to enforce allowed
hosts, honour `Retry-After`, bound retries, redact tokens from errors, or record an external call
in the audit. Building Graph without this seam means building it ungoverned.

### F5 — There is no README

The repository root has no `README.md`. `docs/` has 176 markdown files, including 70 ADRs, 8 LAWs
and a governance freeze — and no entry point that gets somebody from a clean machine to a running
Aurora.

### F6 — `docs/reference/platform-support.md` is stale

It records Discord as **UNVERIFIED** against the real service in every row. As of this session
Discord's gateway, guild and channel listing, voice join with DAVE/MLS, listening with local
transcription, and speaking have all run against real Discord. The document was accurate when
written and is not now.

## What is complete, partial or absent

**Complete and load-bearing:** Kernel and the eight authorization steps; policy; approvals with
operator passphrase; hash-chained audit with anchor and break-sealing; idempotency and
reconciliation; consent sessions and named windows; the event bus with outbox; the vault; SQLite
persistence with forward migrations; plugin manifest reading, installation, lifecycle, service
plugins and confinement on macOS; the cognitive cycle, planner, scheduler, memory, world model,
beliefs, mind state.

**Partial:** Linux confinement (written, never run). `health` covers components but not
configuration, connectivity or credentials — it is the right seam for `doctor` and is not it yet.

**Absent:** outbound HTTP governance; Windows confinement; Windows ACL protection for key files;
any Microsoft integration; README and setup documentation.

## Consequences for this programme

1. **Microsoft 365 belongs in-process, as `ICapability` implementations — not as a plugin.**
   F1 makes the alternative self-defeating: the enterprise capability family would be missing from
   the enterprise platform. In-process also reaches Windows-native credential storage, which a
   confined subprocess should never hold.
2. **The HTTP seam (F4) is a prerequisite**, not a detail. It is built before the first Graph call,
   or the first Graph call establishes the wrong pattern for everything after it.
3. **Windows work (F2, F3) is independent of Microsoft work** and can proceed in parallel; F3 in
   particular is a defect to fix regardless of whether anybody ever installs a plugin there.
4. **Discord stays as it is.** It is the reference for how an external integration is governed, and
   it works.
