# Design 0052 — Confining plugins with the operating system

**Status:** Implemented · **Date:** 2026-08-26
**Closes:** the second open item in `docs/adr/0050-completing-the-platform.md`
**Completes:** `docs/adr/0048` — which stated the boundary honestly and left it where it was

## The gap

RFC 060 rule 2: a plugin runs "without access to the main process, database, vault **or general
network**". Aurora delivered the first three by construction — a separate process, given the
invocation on stdin and nothing else, with the environment cleared so no key path or connection
string travels. The fourth was never delivered at all. Neither was the filesystem: the child ran as
the same user as Aurora and could read the database, the four key files, and everything the owner
owns.

What made this worse than an unimplemented feature is that it was **invisible**. A confined plugin
and an unconfined one produced identical results, so nothing in the system, and nobody reading its
output, could tell which one had just run.

## What was built

An `IPluginSandbox` seam that returns a `SandboxPlan`: the command actually to launch, the level it
achieves, the mechanism doing the enforcing, and — when it achieves nothing — a list of what is not
enforced, in words an owner can act on.

**macOS — `sandbox-exec`.** A deny-by-default SBPL profile that opens exactly three things: reading
the system (an interpreter has to find its own runtime), reading the plugin's installed directory,
and reading and writing its working directory. `/Users` is denied for reading, which is where the
database, the keys and everything personal live. `(deny network*)` is the last rule.

**Linux — bubblewrap.** `--unshare-net` for a network namespace with no route in it, `--unshare-pid`
so it cannot see Aurora, `--cap-drop ALL`, `--die-with-parent`, the system bound read-only, and one
writable bind: its working directory. `bwrap` is looked for at `/usr/bin` and `/bin` and **not**
through `PATH`, because `PATH` is inherited from whoever started Aurora and can name a directory
anybody can write to — a sandbox found that way could be a program that pretends to sandbox.

**Windows and everything else — nothing, said out loud.** Windows confinement means an AppContainer
token through `CreateProcessAsUser`, which `ProcessStartInfo` does not reach. Writing that
unverified and calling it a sandbox would be worse than reporting there is none.

## The decision that matters: what happens when it cannot confine

The host **refuses to invoke**. `sandbox_unavailable`, naming the mechanism that is missing, the
three things that are not enforced, and the setting that accepts them.

This is the opposite of the usual fallback, and deliberately. A seam whose default is "run it
anyway" converts a missing security property into an invisible one — which is exactly the state
this ADR exists to end. Refusing means the gap is discovered by the person who can decide about it,
at the moment they are deciding.

`Aurora:Plugins:AllowUnconfined` accepts it, default `false`. It is the right setting for somebody
running a plugin they wrote themselves, and the wrong one for anything installed.

An unavailable sandbox **does not count against the plugin**. The circuit breaker quarantines after
three consecutive failures; three refusals that never reached the plugin would quarantine it for a
property of the machine, and the owner who then installs bubblewrap would find a plugin still marked
untrustworthy for something it never did.

## What is verified, and what is not

The macOS path is tested by running a real program under the real sandbox and asking the kernel to
stop it: a plugin that tries to open a socket, one that tries to list the owner's home, one that
tries to write outside its directory. All three are refused, and the third still writes inside its
own directory, so the test cannot pass by the plugin simply failing to start. Asserting on the
generated profile text would have passed just as happily against a profile permitting everything.

**The Linux path has not been run.** The machine Aurora was built on is a Mac. The flags are
bubblewrap's documented interface and the policy mirrors the macOS one exactly, but the first person
to run a plugin on Linux runs that code first. The test file says so where somebody will read it,
and `plugin-sandbox` in `/health` reports what this machine actually does rather than what the code
intends.

## The bug this found

The write test failed while the identical commands passed by hand. Two causes, both worth recording:

**A sandbox policy matches the path the kernel arrives at.** On macOS the temporary directory is
reached through `/var`, a symlink to `/private/var`, so a rule naming `/var/folders/…` matches
nothing and the plugin is denied its own directory. `SandboxPaths.Real` now walks every component.

**Wrapping the command lost the working directory.** Rewriting `ProcessStartInfo` to launch the
sandbox instead of the plugin dropped `WorkingDirectory`, so the child inherited Aurora's own
directory and every relative path it used landed somewhere the sandbox correctly refused. The
sandbox was working perfectly; the process it confined was in the wrong place.

## Consequences

`SubprocessPluginHost`'s documentation no longer needs the paragraph beginning "closing the last two
needs an OS sandbox". On macOS and on a Linux with bubblewrap, RFC 060 rule 2 is met in full.
Elsewhere it is refused rather than quietly waived, and `/health` says which.
