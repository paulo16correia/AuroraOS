# Design 0065 — Dead contracts, and a test that keeps them dead

**Status:** Implemented · **Date:** 2026-08-26

## The pattern

Four separate findings in this pass had the same shape: something declared as part of a contract,
compiled into the build, referenced in documentation, and produced by nothing.

- Two security event types that nothing raised (`docs/adr/0064`).
- `ApprovalStatus.Expired`, which did not exist, and whose absence deadlocked a scope
  (`docs/adr/0064`).
- `InstallationStatus.Removed` — a plugin state nothing could reach.
- `PluginRefusal.UndeclaredEndpoint` — a refusal nothing returned.
- `MindStatus`, which declared six states and reached two.

Each took reading to notice, and each was invisible to the test suite, because every part of them
compiled and every test of the part passed.

So they are now asserted rather than looked for. `DeadContractTests` walks the declared events,
states, refusals and security event types by reflection and fails on any that nothing in `src/`
produces. A constant that stops being reachable fails on the day it stops.

## Plugins can be finished with

`REMOVED` was declared and unreachable. Disabling held a plugin and releasing let it run again;
there was no way to be done with one. An owner who no longer wanted a plugin left it `DISABLED` in
the catalogue for ever.

`RemoveAsync` ends an installation and takes back every granted permission. Two things about it:

**The row stays.** What Aurora once ran, and what it was granted while it ran, is part of how this
instance got here. An installation log that forgets removals cannot answer "what was this machine
doing in March".

**It is terminal.** Releasing now refuses a removed installation. Without that check, `ReleaseAsync`
would return any installation to `INSTALLED` — so a removed plugin could be let back in through the
door meant for a quarantined one, restoring permissions the owner had taken away. That bypass was
latent only because nothing could set `REMOVED` in the first place.

## The Mind stopped claiming to be two things

`MindStatus` declared `INITIALIZING`, `ACTIVE`, `DEGRADED`, `PAUSED`, `RECOVERING` and `RETIRED`,
following RFC 020. Only `ACTIVE` and `PAUSED` were ever assigned.

The reason the other four never happened is that three of them are not the Mind's business.
`SelfModel.OperationalState` owns degraded and recovering (RFC 027); `InstanceState` owns
bootstrapping and restore (RFC 039). Declaring them again on the Mind would have given "is Aurora
degraded" two answers that drift apart — the same duplicate-source-of-truth problem that made the
belief and goal lists worth removing earlier in this pass.

So `MindStatus` is now the three states the aggregate actually owns: `ACTIVE`, `PAUSED`, `RETIRED`.
Retiring is implemented, needs an actor and a reason, and is terminal — what came back after a
retirement would be a different entity wearing the same identity, which is the one thing LAW-008
exists to prevent.

This is a narrowing of an RFC by implementation, and it is recorded here rather than done quietly.

## A plugin cannot ask for the network

RFC 060 rule 1 asks a plugin to declare its network domains. Rule 2 says a plugin runs without the
general network. Aurora resolves the two strictly: the sandbox denies the network outright
(`docs/adr/0052`), so there is no endpoint Aurora could grant.

That left `network_endpoints` as a field an author could fill in and Aurora would ignore — a
declaration that looks like a limit being enforced and is not. A manifest declaring one is now
refused, at validation so the author is told while writing the file, and again at verification so a
manifest that reached the registry another way is refused too.

`UNDECLARED_ENDPOINT` is what that refusal returns, which is how a declared refusal reason became a
produced one.

## Local-only is asserted over the source

The same class of problem, one level up: Aurora reaches nothing outside this machine, and that
rested on somebody having grepped for it once.

`LocalOnlyTests` reads the source tree and fails on any construct that could open an outbound
connection, with the single loopback call named explicitly — the console's health verb, pinned to
`127.0.0.1` as a literal so no configuration can point it elsewhere. It also asserts Kestrel binds
to loopback and that the host-header guard is installed.

Unusual for a test, and worth it. Local-only is the property every other guarantee is built on, and
the way it would be lost is one line in one pull request that nobody thought about.

## The test suite was filling the disk

Not a contract problem, but found the same afternoon and worth recording, because it presented as
the suite being broken.

Tests scattered key files, anchor files and sandbox roots directly into the system temporary
directory and deleted almost none of them: 5,800 leftovers from one afternoon's runs, and the
reason a suite that had passed a hundred times started failing at random with SQLite unable to
write.

Individually tiny. Collectively, the machine ran out of room.

`TestTemp` gives every test a path under one per-process directory that is removed on exit. A test
that wants a path asks for one and does not have to remember to clean it up — because remembering
is exactly what did not happen.
