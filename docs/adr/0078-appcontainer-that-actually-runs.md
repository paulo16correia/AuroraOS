# Design 0078 — The AppContainer, actually run

**Status:** Implemented, verified on Windows · **Date:** 2026-09-07
**Rests on:** `docs/adr/0072` (confinement that proves itself), `docs/adr/0075` (what runs a script
plugin), `docs/adr/0003`/`0010`/`0036` (the sandbox file writer and reader)

## Why now

`docs/adr/0072` built Windows plugin confinement and said, honestly, that no line of its interop
had met a Windows kernel — the whole file was UNVERIFIED. This sprint ran it: a real hostile plugin,
inside the real `WindowsAppContainerSandbox`, driven through the real `ServicePluginHost`, on a
Windows machine. It found three defects that every off-Windows test had passed straight over,
because each is a single value the code could carry correctly right up until a kernel judged it.

## Three defects a kernel had to find

**1. A confined `CreateProcess` needs `LOCALAPPDATA`.** Aurora builds a plugin's environment rather
than inheriting it (`docs/adr/0075`), and the built one named `PATH` and `SystemRoot`. Creating a
process *inside an AppContainer* also reads `LOCALAPPDATA` to compose the container's private
storage path, and with it absent `CreateProcess` fails with `ERROR_ENVVAR_NOT_FOUND` (203) — before
the process exists, so it surfaces as "the confined process could not be created" with nothing
naming the cause. Isolated by varying the environment one variable at a time against the same
target. The variable need not lead anywhere real, so Aurora sets it to the plugin's own working
directory: present, and disclosing nothing about the owner.

**2. `CreateAppContainerProfile`'s "already exists" code was wrong by two hex digits.** The reuse
branch compared against `0x800700B5` (`ERROR_ALIAS_EXISTS`, returned by nothing here); the value a
re-created profile actually returns is `0x800700B7` (`HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS)`). So
the branch that opens an existing container never ran, and every start after a plugin's very first
failed with `0x800700B7`. A plugin runs many times; this made confinement work at most once.

**3. The internetClient capability was the wrong SID.** It was built with
`CreateWellKnownSid(116)`, believed to mean `WinCapabilityInternetClientSid`. On this Windows, 116
is a mandatory-label SID (`S-1-18-5`); enumerating the whole range found `CreateWellKnownSid`
produces no `S-1-15-3-*` capability SID at all. The malformed capability made `CreateProcess` reject
the whole creation with `ERROR_INVALID_PARAMETER` (87), so **no networked plugin** — Voice,
Microsoft, Discord — could ever start confined. Capability SIDs are fixed and documented, so Aurora
now builds internetClient from its string, `S-1-15-3-1`, with `ConvertStringSidToSid`; no
version-specific enum is involved.

All three are one-line values. That is the point of `docs/adr/0072`'s design: the process is created
suspended and every one of these produced a refusal or a failed creation rather than an unconfined
plugin that Aurora reported as confined. The fail-closed posture held; the code was simply never
able to *succeed* until now.

## What the kernel actually enforced, once it could run

With the three fixed, a hostile probe plugin was started inside the container and told to attempt
every violation. The Windows kernel's answers:

- **Token**: the child is an AppContainer, its container SID is the one Aurora created (checked both
  by Aurora's own `AppContainerVerdict` before `ResumeThread`, and independently by the plugin from
  inside its own token), and it is not elevated.
- **Filesystem**: reading or writing Aurora's database and vault key — DENIED. Another plugin's
  directory — DENIED. Anything outside the working directory, the owner's home, the temp directory,
  system directories — DENIED. Its own working directory — writable. Its program and interpreter
  directories — listable and executable, not writable.
- **Reparse points**: junctions Aurora pre-planted inside the working directory, leading to Aurora's
  state and to another plugin, were all DENIED — the container has no access to the target, so the
  link leads nowhere it may go.
- **Network, no grant**: the loopback control plane — DENIED (the connection is dropped, it times
  out). **Network, with grant**: `CreateProcess` now succeeds with the internetClient capability;
  the loopback control plane is **still** DENIED, because internetClient does not carry loopback and
  Aurora never grants `privateNetworkClientServer`. Outbound reachability to the internet could not
  be exercised — the verification machine has no DNS, so even unconfined Aurora cannot resolve a
  name — and is recorded UNVERIFIED rather than claimed.
- **Job Object**: a grandchild the plugin detached to outlive its call was alive while Aurora held
  the job and dead two seconds after the host was disposed. `KILL_ON_JOB_CLOSE` works.
- **Child processes**: a process the plugin spawned was itself an AppContainer — children inherit
  the container rather than escaping it. A plugin can launch a system executable such as `cmd.exe`,
  but it launches confined, with no more reach than the plugin, and this is not an Aurora capability
  or a path to one.

## Confinement needs a grantable interpreter, and says so when it hasn't one

An AppContainer reaches no file whose ACL does not name its SID, so Aurora grants the container
read-and-execute on the interpreter's directory. On the verification machine the system Python lives
under `C:\Python313`, whose ACL a non-administrator cannot change — so the grant fails, and Aurora
**refuses the plugin** (`SERVICE_UNAVAILABLE`, naming the directory) rather than running it
unconfined. That is the correct fail-closed outcome, and it is also an operational fact worth
stating: on Windows, Aurora confines plugins only when their interpreter lives somewhere Aurora can
grant the container access — a per-user Python, or a directory an administrator has opened to
application packages. The whole verification was therefore run against a user-owned copy of Python,
which Aurora could grant.

## The hard-link question, answered with evidence

A hard link is a second name for one file, sharing one security descriptor; it is not a reparse
point, so the sandbox's link-component check does not see one. The question `docs/adr/0072` left
open: can a plugin use one to cross the boundary?

Measured, on Windows:

- **`CreateHardLink` requires write access to the target.** A confined plugin, which has only
  read-and-execute on its interpreter and program and no access at all to Aurora's secrets, was
  DENIED creating a hard link to any of them. It can only hard-link files inside its own working
  directory, which it already fully controls.
- **Order matters, and only one order is dangerous.** Granting the working directory and *then*
  hard-linking an outside file into it leaves the outside file's ACL untouched. Hard-linking an
  outside file into the directory *first* and then granting it — which is what `GrantPaths` does on
  every start, re-propagating the inheritable ACE to existing children — stamps the outside file's
  shared security descriptor with the container's grant. So a hard link a plugin could leave behind
  across a restart *would* be pulled into the grant.
- **But the plugin cannot leave that behind.** The only files it can hard-link are ones it can
  already write, i.e. ones already inside its full-control working directory. It cannot plant a link
  to a file it is not already allowed to write, and Aurora's secrets are writable by no one but the
  owner.
- **`SandboxFileWriter` is safe against a planted link regardless.** It writes via a temp file and a
  replacing rename (`MoveFileEx(MOVEFILE_REPLACE_EXISTING)`), which swaps the directory entry rather
  than writing through it. A hard link planted at the destination has its own name replaced; the
  linked-to file keeps its content. Proven by test, and now a regression test.
- **`SandboxFileReader` will read through a hard link at the destination** — a hard link is not a
  reparse point, so nothing stops it. This is a genuine defence-in-depth gap and it is *not* a
  boundary crossing: to plant such a link at all, an actor needs write access both to the owner-only
  sandbox root (a separate tree from the plugin working directories, which a confined plugin cannot
  write) and to the target file. Both require being Aurora's own owner — who already holds every
  secret the link could point at. A hard link cannot disclose a file its planter could not already
  read and write directly.

**Verdict: SAFE under Aurora's threat model.** No production change is made to chase it, because the
only fix available — detecting a hard link at the final component — needs a link-count query that is
not portable and would add Win32 interop to a security-sensitive path to defend a case no
lower-privileged actor can reach. The reasoning is recorded here instead, and the write-path safety
is pinned by a regression test.

## What is now true on Windows

`docs/reference/platform-support.md` moves Windows plugin confinement from UNVERIFIED to VERIFIED,
with the evidence above and two honest caveats: outbound network reachability with a grant is
UNVERIFIED (no internet on the verification machine), and confinement requires an interpreter Aurora
can grant — a system-wide install a non-administrator cannot re-ACL is refused, fail-closed.
