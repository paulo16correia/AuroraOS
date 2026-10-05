# Aurora OS — Operator Runbook (Windows-first)

This is the practical guide to installing, configuring, and running Aurora OS, and to preparing a
controlled demo. It is written so a new operator can follow it without reading the RFC collection.
It is Windows-first; where a step differs on macOS or Linux that is called out.

It never contains a real credential. Every place a secret would go is shown as a placeholder.

Status vocabulary used throughout (the same words `docs/reference/platform-support.md` uses):

- **IMPLEMENTED** — the code exists.
- **TESTED** — an automated test exercises it.
- **VERIFIED** — a test has actually run on that platform, or the behaviour was observed live there.
- **UNVERIFIED** — implemented and tested in principle, but not run/observed on that platform.
- **UNSUPPORTED** — deliberately not offered; Aurora refuses rather than degrading.

---

## 1. Prerequisites

**Operating system.** Windows 10/11 or Windows Server with the AppContainer APIs present (every
supported desktop/server build has them). Aurora also runs on macOS and Linux; plugin confinement
is VERIFIED on macOS (`sandbox-exec`), on Windows (AppContainer) and on Linux, where it needs
bubblewrap (`sudo apt install bubblewrap`) and plugins are refused without it.

**.NET.** The .NET 10 SDK to build, or the .NET 10 runtime to run a published build. Confirm:

```bash
dotnet --version
```

**Python (required for the shipped plugins).** Aurora's Voice, Microsoft 365 and Discord plugins
are Python programs. You need CPython 3 available as `python3` (or `python`).

> **IMPORTANT — the interpreter must be one Aurora can grant to an AppContainer.**
> On Windows a confined plugin runs inside an AppContainer, which reaches a file only if that
> file's directory names the container's SID. Aurora adds that grant at launch — but only if the
> account running Aurora can change the interpreter directory's ACL.
>
> A **system-wide install such as `C:\Python313`** is owned by Administrators and a non-administrator
> **cannot** re-permission it. Aurora then refuses the plugin, fail-closed, with
> `SERVICE_UNAVAILABLE: '<dir>' could not be granted to the container` — which looks like a broken
> plugin but is a directory-permission problem.
>
> Use one of:
> - a **per-user Python** (install "for me only", which lands under `%LocalAppData%`), or
> - a Python directory an **administrator has granted** read-and-execute to `ALL APPLICATION
>   PACKAGES`, or
> - name the interpreter in `Aurora:Plugins:Interpreters:python3` (§3) — the cheapest of the
>   three, needs no administrator, and does not touch what is installed.
>
> `doctor` (§8) checks this for every installed plugin and names the exact directory if it
> cannot be granted.

**Native dependencies (only for the features that use them).**

- **Discord voice** needs `libopus` (native) and a local speech-to-text. Without them, voice
  reports itself unavailable rather than failing mid-call.
- **Voice** needs `whisper.cpp` with a model beside the plugin, a local Ollama model, and an
  ElevenLabs key for speaking — speech is the one leg that leaves the machine. All optional; the
  plugin reports what is missing through `voice.status` without starting anything.

None of these are needed for text-only Microsoft 365 or Discord messaging.

---

## 2. Installation

```bash
# From the repository root:
dotnet build Aurora.slnx -c Release
```

Run the server from the built `Aurora.Server`:

```bash
dotnet run --project src/Aurora.Server -c Release
```

### There is no `aurora` on your PATH

Every operator command in these guides is the same program with a verb — `secret`, `plugin`,
`doctor`, `backup`, `health`, `enroll-passphrase` — reached the same way:

```bash
dotnet run --project src/Aurora.Server -- <verb> …
```

Nothing installs a shorter name, and these guides used to print one as though something did. If you
are going to type it often, make the short name yourself rather than wait for Aurora to:

```powershell
# PowerShell, for this session — or put it in $PROFILE
function aurora { dotnet run --project src/Aurora.Server -- @args }
```

```bash
# bash or zsh
aurora() { dotnet run --project src/Aurora.Server -- "$@"; }
```

It has to run from the repository root either way, because the path to the project is relative.

On first start Aurora creates its data directory and, in it, the database, the key files, the audit
anchor, and the plugin and sandbox roots. With no configuration these all live under:

```
%LocalAppData%\Aurora\
  aurora.db                     the database (SQLite, WAL)
  aurora.db.anchor              the external audit-chain anchor
  aurora.audit.key              audit HMAC key      (owner-only)
  aurora.vault.key              secret encryption   (owner-only)
  aurora.genome.key             genome signing      (owner-only)
  aurora.snapshot.key           snapshot encryption (owner-only)
  aurora.deliberation.key       deliberation        (owner-only)
  aurora.plugin.key             plugin manifest seal(owner-only)
  aurora.passphrase.json        operator passphrase verifier (owner-only)
  plugins\                      per-plugin working directories
  sandbox\                      the file-capability sandbox root (owner-only)
```

`%LocalAppData%` is a per-user location, which is why the defaults satisfy the owner-only and
grantable-directory requirements without any extra work. **Keeping the defaults is the recommended
setup.** If you relocate the data directory, put it somewhere the running account can restrict its
own files (see §4).

---

## 3. Configuration

Aurora reads standard .NET configuration: `appsettings.json` beside the server, environment
variables (with `Aurora__` as the section separator, e.g. `Aurora__Port`), and command-line
arguments. All keys are optional; sensible per-user defaults apply.

| Key | Meaning | Default |
| --- | --- | --- |
| `Aurora:Port` | Loopback port for MCP/API/panel | `5099` |
| `Aurora:BearerToken` | The agent's token (or env `AURORA_BEARER_TOKEN`) | generated per run, printed once |
| `Aurora:DbPath` | Database file | `%LocalAppData%\Aurora\aurora.db` |
| `Aurora:SandboxRoot` | File-capability sandbox root | `%LocalAppData%\Aurora\sandbox` |
| `Aurora:PluginRoot` | Per-plugin working directories | `<db dir>\plugins` |
| `Aurora:AuditKeyPath` / `AuditAnchorPath` | Audit key and anchor | beside the database |
| `Aurora:VaultKeyPath` / `GenomeKeyPath` / `SnapshotKeyPath` / `DeliberationKeyPath` / `PluginKeyPath` | Key files | beside the database |
| `Aurora:PassphrasePath` | Operator passphrase verifier | beside the database |
| `Aurora:Plugins:AllowUnconfined` | Run plugins even where the OS cannot confine them | `false` |
| `Aurora:Plugins:Interpreters:python3` | The interpreter to confine script plugins against | from `PATH` |
| `Aurora:HeartbeatSeconds` | Upkeep interval (0 disables) | `300` |
| `Aurora:Voice:Enabled` | Whether voice runs at all | `false` |
| `Aurora:Voice:MaxConcurrentSessions` | Across every channel, not per channel | `2` |
| `Aurora:Voice:MaxSessionDuration` | The longest a grant may ask for | `00:15:00` |
| `Aurora:Model:Endpoint` | Where the local model runtime answers. Must be this machine | `http://127.0.0.1:11434` |
| `Aurora:Model:Name` | The model to ask for, as the runtime names it | `llama3.1:8b` |

### Naming the interpreter

Aurora resolves `python3` from `PATH` and from a script's shebang, and on Windows will not accept an
interpreter path from a **manifest** — a plugin does not get to choose what runs it. The owner does:

```
Aurora:Plugins:Interpreters:python3    (absent by default)
```

A name here is authoritative. If the file is not there, that is a refusal rather than a reason to go
looking for a different Python, which is the point: the thing being prevented is a plugin being
confined against an interpreter nobody chose.

This is the answer to the Windows blocker in §2, and it is worth saying plainly because this section
used to claim no such setting existed. On a machine with a system-wide Python that `PATH` finds first
and a per-user one beside it, `doctor` fails both plugins and the remedy is one line — not an
administrator, and not reinstalling anything:

```powershell
$env:Aurora__Plugins__Interpreters__python3 = "$env:LocalAppData\Aurora\runtime\python\python.exe"
```

Otherwise: keep the interpreter you want first on `PATH`, or install it per-user.

---

## 4. Security prerequisites

These hold automatically with the default per-user data directory. If you change paths, confirm
each with `doctor`.

- **AppContainer confinement (Windows).** Plugins run inside a per-plugin AppContainer, created
  suspended, its token verified before it is allowed to run, torn down if verification fails. It
  reaches only its own working directory (read/write), its program and interpreter directories
  (read/execute), and — with an explicit grant — the internet, never Aurora's loopback control
  plane. VERIFIED on this project's Windows machine (docs/adr/0078).

- **Owner-only key material.** Every key file and the passphrase verifier are readable by the
  running account alone. This is now **fail-closed**: if Aurora cannot establish owner-only
  protection on a key, it refuses rather than continuing with a key behind a wider permission
  (F-3, docs/adr/0079). Put the data directory on a volume where the account can restrict its own
  files — a per-user location does this; a shared network volume may not.

- **Loopback-only control plane.** Kestrel binds `127.0.0.1` only, behind an anti-DNS-rebinding
  guard and bearer/operator authentication. Aurora is not reachable from another machine, by
  design; do not put it behind a reverse proxy that exposes it.

- **No generic execution.** There is no shell capability and no arbitrary process launch. Plugins
  are the only out-of-process code, and only through the confined host.

---

## 5. Secrets

Secrets are stored encrypted in Aurora's vault (AES-256-GCM) and provisioned from the console —
never over HTTP, never in a config file, never in this document.

A plugin declares the secrets it needs, by name, in its manifest. You provision each one under the
purpose `plugin/<plugin_id>/<secret_name>`:

```bash
# Placeholders — substitute the real plugin id, secret name, and value.
dotnet run --project src/Aurora.Server -- secret set plugin/discord bot_token
dotnet run --project src/Aurora.Server -- secret list
```

Required vs optional: a secret in a plugin's `required_secrets` must be present or the plugin will
not start — `doctor` reports a missing one as **FAIL**. Anything else is optional.

Shipped plugins and their required secrets (names only):

- **plugin/discord** — `bot_token`
- **plugin/microsoft** — `tenant_id`, `client_id`, `refresh_token`
- **plugin/voice** — `provider_auth_token`, `openai_api_key`

---

## 6. Plugins

```bash
# Validate a plugin folder (checks manifest, interpreter, capabilities):
dotnet run --project src/Aurora.Server -- plugin validate src/Aurora.Server/plugins/discord

# Install it (records it and seals its manifest):
dotnet run --project src/Aurora.Server -- plugin install src/Aurora.Server/plugins/discord

# List installed plugins:
dotnet run --project src/Aurora.Server -- plugin list
```

> **A plugin takes effect on the next start.** Installation is a deliberate, console-only decision
> that records third-party code in Aurora's catalogue; the running instance does not pick it up
> until it restarts. Plan installs before a demo, not during one.

To check a plugin's live readiness (native deps, sign-in) once Aurora is running, call its own
status capability through your MCP client — for example `discord.voice.status` or `voice.status`.

To diagnose a plugin that will not start, run `doctor` (§8): it reports manifest validity,
interpreter resolution, whether the interpreter directory is grantable, and whether required
secrets are present, naming the exact plugin and path at fault.

---

## 7. Connecting an MCP client

Aurora exposes MCP over streamable HTTP on the loopback port:

```
Endpoint:      http://127.0.0.1:5099/mcp
Transport:     Streamable HTTP
Authorization: Bearer <the token printed at startup, or Aurora:BearerToken>
```

The bearer token is the agent's credential. If you did not set `Aurora:BearerToken` (or
`AURORA_BEARER_TOKEN`), Aurora prints a generated one on startup — copy it then; it is not stored.
Configure your MCP client with that endpoint and an `Authorization: Bearer …` header.

The fixed tools are `aurora_catalog`, `aurora_execute`, `aurora_converse`, `aurora_review`,
`aurora_self`, `aurora_cycle`, and `aurora_approve`. There is no generic shell or raw tool.

The operator control panel is separate from the agent: start the server with the `ui` argument and
open the single-use link it prints (valid ten minutes). The agent's bearer token cannot reach the
panel's decisions.

### Who decides an approval

A person, on one of two surfaces:

- **The control panel.** The *Approvals & tools* tab lists each pending request — the action, what
  it reaches and the exact input it would run with — with **Approve this request** and **Reject
  it**. Its session is a credential the agent never holds, so it decides with or without a
  passphrase, and asks for the passphrase when one is enrolled.
- **`aurora_approve`, with the operator passphrase.** Enrol one on the server's console:

  ```bash
  dotnet run --project src/Aurora.Server -- enroll-passphrase
  ```

  Where the machine has a desktop prompt, Aurora asks for it there rather than taking it from the
  tool call.

With no passphrase enrolled, `aurora_approve` answers `passphrase_not_enrolled` and the decision is
made in the panel: the tool is the agent's, and only a secret the agent does not hold tells a person
apart from it (docs/adr/0088).

---

## 8. `doctor`

`doctor` is the deployment preflight. Run it **before** starting plugins — it turns the failures you
would otherwise hit at first plugin call into an explicit list:

```bash
dotnet run --project src/Aurora.Server -- doctor
```

It checks the database path, every key file (present and **actually** owner-only, not merely
"the mechanism exists"), the sandbox root, the confinement mechanism, each installed plugin's
manifest/interpreter/interpreter-directory-grantability, and each required secret's presence (never
its value). It reads state and changes nothing.

Each line is one of:

- **PASS** — the property holds.
- **WARN** — worth your eye, but not a blocker for a controlled demo (e.g. unconfined plugins are
  enabled, or a directory is not owner-only but the path defences still apply).
- **FAIL** — a plugin or Aurora will not work correctly until you fix it. `doctor` exits non-zero
  if any check FAILs, so it can gate a scripted start.
- **INFO** — a fact (e.g. a key that is absent and will be created owner-only on first start).

Common FAILs and their fix:

| Line | Cause | Fix |
| --- | --- | --- |
| `… interpreter … cannot grant its directory` | system-wide Python a non-admin cannot re-ACL | set `Aurora:Plugins:Interpreters:python3` to a per-user Python (§3), or have an admin grant the directory |
| `… secret '…' missing` | a required secret is not provisioned | `secret set plugin/<id>/<name> <value>` |
| `… key: present but NOT owner-only` | data directory on a volume that will not restrict | move the data directory to a per-user location |
| `… program is not where the manifest says` | plugin files moved after install | reinstall the plugin from its folder |

---

## 9. Demo preparation

1. **Start clean.** Decide the data directory (default per-user is fine) and set
   `Aurora:BearerToken` so the token is stable for your client.
2. **Install plugins**, then **restart** so they are picked up.
3. **Provision credentials** for the plugins you will demo (`secret set …`).
4. **Run `doctor`** and resolve every FAIL. WARN items are acceptable for a controlled demo.
5. **Start Aurora**, connect your MCP client (§7), and confirm `aurora_catalog` lists the expected
   capabilities.
6. **Warm the plugins** you will use: the first call to a service plugin pays a few seconds of
   Python start-up; make that first call before the audience is watching.
7. **Verify external connectivity and native deps** for anything that reaches outside — call the
   plugin's `*.status` capability. A machine without internet or without a plugin's credentials
   will fail at the external hop, not inside Aurora.
8. **Know what is VERIFIED vs UNVERIFIED** (see §10) and demo accordingly.

---

## 10. Microsoft 365 / Discord / Voice — honest status

The full matrix is in `docs/reference/platform-support.md`. Summary for a demonstrator:

**Microsoft 365** — IMPLEMENTED (54 capabilities across mail, calendar, files, tasks, people,
Teams). TESTED against a loopback stand-in that imitates Graph, including its bad days. It has
**never met a real Microsoft tenant** — live use is UNVERIFIED on every platform. The plugin runs
out-of-process; Aurora itself stays local-only.

**Discord** — messaging and gateway are TESTED against a loopback stand-in on all platforms.
Against the **real** service Discord is VERIFIED on **macOS only**; Windows and Linux are
UNVERIFIED. Voice turn-taking/governance and the cipher/codec are VERIFIED against vectors and a
real round trip, but **voice against real Discord is VERIFIED on macOS only**, and needs `libopus`
plus a local STT present.

**Voice** — IMPLEMENTED and TESTED through the real plugin host and the real Kernel, with the
recogniser and speaker scripted and the model reached over HTTP on loopback. The telephone is gone:
there is one conversation, made of whisper, a model on this machine, and ElevenLabs. Running it for
real needs those installed and an ElevenLabs key, and has **not** been verified end-to-end — nothing
here has met the real speech service, and no whisper model is installed beside the voice plugin yet.
Voice is off until `Aurora:Voice:Enabled` is set.

**Do not claim Windows live verification for Discord/Microsoft/Voice** — it has not happened. Demo
these against their stand-ins, or on macOS where the real Discord path is verified, and say which.

---

## 11. Troubleshooting

- **`SERVICE_UNAVAILABLE: '<dir>' could not be granted to the container`** — the interpreter (or
  program) directory cannot be re-permissioned by the running account. Name a per-user interpreter
  in `Aurora:Plugins:Interpreters:python3` (§3), or have an administrator grant the directory to
  application packages. This is fail-closed, correct behaviour, not a crash.
- **A required secret is missing** — the plugin starts "degraded" or refuses; `doctor` names
  it. Provision it with `secret set plugin/<id>/<name> <value>` and restart.
- **Plugin fails to start on Windows with a `Win32Exception`** — the interpreter could not be
  resolved. Ensure `python3` is on `PATH` (per-user install), or name it in
  `Aurora:Plugins:Interpreters:python3` (§3), then re-run `doctor`.
- **Missing native dependency** (`libopus`, a speech engine) — the feature reports itself
  unavailable via its `*.status` capability; install the dependency. Optional features never fail
  the whole plugin.
- **External service unavailable / DNS errors** — the demo machine has no route or no credentials
  to the external service. Aurora's local path is unaffected; only the external hop fails.
- **AppContainer refusal** — Aurora verified the container was not the one it created, or the API
  is unavailable, and terminated the plugin before it ran. Check the Windows build has AppContainer
  support; do not set `AllowUnconfined` to work around confinement on a real deployment.
- **"took effect on restart" confusion** — a freshly installed plugin is not live until Aurora
  restarts. This is by design.
- **Aurora refuses to start citing owner-only protection** — a key file could not be restricted to
  the owner. Move the data directory to a per-user location (F-3). Do not work around it.

---

## 12. Controlled-demo checklist

```
[ ] Data directory chosen (per-user default recommended)
[ ] Aurora:BearerToken set so the MCP client has a stable token
[ ] Per-user Python on PATH (NOT a system dir a non-admin cannot re-ACL)
[ ] Plugins installed, then Aurora RESTARTED
[ ] Required secrets provisioned (secret set …), none in any file
[ ] `doctor` run — 0 FAIL (WARN reviewed and accepted)
[ ] Aurora started; MCP client connected; aurora_catalog lists expected capabilities
[ ] Service plugins warmed with one call each
[ ] External connectivity + native deps confirmed via each plugin's *.status
[ ] Demo scoped to VERIFIED paths; UNVERIFIED integrations flagged as such
[ ] Backup taken if the demo will write state you care about (backup <dir>)
```
