# Design 0072 — Confinement that proves itself

**Status:** Implemented, unverified · **Date:** 2026-09-01
**Rests on:** `docs/adr/0052` (plugin confinement), `docs/adr/0071` (Microsoft 365 is a plugin)

## Why now

Design 0071 settled that Microsoft 365 is a plugin, because the alternative was Aurora's own
process opening sockets. That made Windows plugin confinement the prerequisite for every enterprise
capability rather than a platform nicety at the end of the list. Windows had none.

## The seam was Unix-shaped

`IPluginSandbox.Plan()` returned a file name and a list of arguments. That was not a simplification
— it was an accurate description of what confinement *is* on the two platforms Aurora had:
`sandbox-exec` and `bwrap` are programs that apply a policy and then become the plugin. Confinement
fitted in a command line because it was one.

An AppContainer is a property of the token a process is created with. It is reached through
`CreateProcess` with `PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES`, and `ProcessStartInfo` exposes
nothing of the sort. No command line expresses it.

So the seam gained `StartAsync` beside `Plan`. `Plan` stays, because "what would this machine do to
a plugin" is a question the health report and the install prompt ask without wanting it done. The
three existing sandboxes share `WrapperSandbox` and kept the behaviour they were verified with; the
hosts hand over a `SandboxLaunch` and get back a process or a refusal.

## Suspended, then questioned, then resumed

The requirement that shaped everything else: **a plugin must never execute an instruction before
Aurora has demonstrated that it is confined.**

Verifying after launch sounds equivalent and is not. It makes the interesting failure the one where
a plugin does its work in the milliseconds before the check catches up — and that is precisely the
failure an attacker would aim for, because it is the only one that pays.

So:

1. `CreateProcess` with `CREATE_SUSPENDED`. The process exists and has run nothing.
2. `OpenProcessToken`, then ask it two questions: is this an app container, and which one?
3. `AppContainerVerdict` judges the answers.
4. Confined → `ResumeThread`. Anything else → `TerminateProcess`, and a refusal.

There is no state to unwind on the failing path, because nothing ran.

**Every failure is read as refusal, never as benefit of the doubt.** A token that cannot be opened
is not "probably fine" — it is a confinement that was not demonstrated. That branch is where an
optimistic reading would let an unconfined plugin run, and it is tested.

## What is granted, exactly

An AppContainer reaches no filesystem it has not been named on, which makes deny-by-default real
rather than configured. Aurora names two paths and no others:

| Path | Access | Why not more |
| --- | --- | --- |
| The plugin's own directory | Full control | Everything it writes, and the only place it may. |
| Where its program lives | Read and execute | With write, a plugin could rewrite its own installed code and the manifest hash would describe something that no longer runs. |

Capabilities: `internetClient`, and only when the owner granted the network. Windows publishes
dozens — the camera, the microphone, the owner's documents — and a plugin has no business with any
of them, because what it needs from the machine arrives through Aurora. The set is an enum so that
adding a second is a change somebody reviews.

Handles: exactly three, named individually through `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`. Without it,
`bInheritHandles: true` gives the child every inheritable handle Aurora happens to be holding.

Lifetime: a job object with `KILL_ON_JOB_CLOSE`, which is what bubblewrap's `--die-with-parent`
buys on Linux — a plugin that outlives the thing governing it is an unconfined program with a token
nobody is watching.

## Windows is stricter than macOS in one place

An app container is refused loopback by Windows, capability or not. So a plugin granted the network
on Windows can reach Discord and cannot reach Aurora's own MCP endpoint on 127.0.0.1. On macOS that
boundary rests on the plugin not knowing the port. This is the one respect in which the newer,
unverified implementation is the stronger of the two.

## What none of this has done

**Run.** It was written on a Mac. No line of the interop has met a Windows kernel.

That is why the verification is shaped the way it is. If the interop is wrong — the attribute list
malformed, the SID misread, the handle list rejected — the first Windows machine to try it
terminates the child and refuses with the missing property named. The failure mode of untested
confinement code is a plugin that does not start, not a plugin that runs free while Aurora reports
it confined. That was the design goal for code that could not be tested, and it is not the same
thing as the code being right.

`docs/reference/platform-support.md` says UNVERIFIED and lists what a real run would settle.

## Tested from anywhere, and what that is worth

The implementation is split so that everything *deciding* how much authority a plugin gets is pure:
`AppContainerProfiles` (which capabilities, which paths) and `AppContainerVerdict` (whether a
created process may run). Seventeen tests cover them on whatever machine the suite runs on,
including every branch by which a process can fail verification.

What is left in the interop is the asking. A green suite means Aurora's decisions about confinement
are right. It says nothing about whether Windows honours them.
