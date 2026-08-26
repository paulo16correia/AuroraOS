# Design 0050 — Completing the platform

**Status:** Implemented, with two items that cannot be closed here · **Date:** 2026-08-26
**Closes:** the LAW-007 and LAW-005 gaps in `docs/adr/0027`, the open item in `docs/adr/0011`

## LAW-007 across the platform, not only at the bus

ADR 0027 recorded this law as **half-satisfied** and said so plainly: the outbox enforced its
contract while eight services changed state in silence. That was true for a long time, and it is the
gap that mattered most, because a law enforced at one chokepoint and nowhere else is a law about
that chokepoint.

Nine producers now publish: missions, beliefs, relationships, development, life history, planner,
identity, self — alongside the kernel and memory that already did. Each has a test that drives it
through its **real path** and asserts the event arrives. "The source contains a `PublishAsync`" is a
different claim, and the weaker one.

Two decisions in the payloads worth stating:

**What travels is the identifier and the state, never the content.** A challenged belief announces
that it was contradicted, not what it claimed. A relationship that ended does not name who it was
with — a third party's name is not something to broadcast because their relationship ended. A goal
announces its status, not the outcome its owner phrased. The bus is read by more consumers than the
store is, and a state change is a reason to react, not a licence to copy.

**Self publishes on a transition and never on a reading.** It refreshes on every capability check;
an event per reading would make the bus a log of Aurora looking at itself, and the one moment
somebody needed to hear about would be buried in it.

## LAW-005: the tenant was implicit, which is not the same as absent

ADR 0027 argued that inventing a constant field to satisfy a checklist would be worse than recording
the reason. That was half right and half convenient.

The law's justification is that **orphan state is the mechanism by which agent systems become
impossible to debug or erase**. A tenant that is implicit is a tenant nobody can filter or delete
by. `Tenant.Local` is carried on every event and refused if it names another: present and constant,
multi-tenancy is a data change; absent, it is a redesign.

## The trusted prompt, open since ADR 0010

The oldest open item in the repository. The passphrase raised the bar from "the agent can approve
itself" to "the agent must obtain a secret it was never given" — but the agent still composed the
prompt and carried the answer.

`NativeDialog` asks in a window **the operating system draws**, from arguments Aurora passed, in a
process the agent has no handle on: `osascript` on macOS, `zenity`/`kdialog` on Linux, PowerShell on
Windows. When a machine has one, the passphrase is asked for there and the tool call carries none.

What this is and is not:

- It **does** close the gap that mattered: the agent composes neither the question nor the answer,
  and never sees the secret.
- It is **not** a signed, tamper-proof window. A local attacker already running code as this user
  can interfere with it, and nothing achievable locally changes that.
- It does **not** prove a human is present. Nothing available locally does.

A dismissed dialog is a **refusal**, not an absence of one — treating "they closed it" as "ask again
later" is how a prompt becomes something people click through. Where no desktop tool exists, the
supplied passphrase still works: headless is a real deployment, and refusing to work there would
push somebody back to no passphrase at all.

The same mechanism carries M5's missing alerts. Maintenance notifies **for an incident and nothing
else**: a notification per upkeep pass is a notification people turn off, and then the one that
mattered arrives silenced.

## A test that would have put a password prompt on your screen

Wiring the dialog in made an integration test hang for thirty seconds. It was working correctly —
`osascript` exists on macOS, so the kernel went and asked. The test factory now injects a prompt that
is never available. A suite that opens windows is a suite people stop running.

## The two that cannot be closed here

*(Both were closed the next day: the Azure adapter was removed rather than validated
— `docs/adr/0051` — and plugin confinement was implemented — `docs/adr/0052`. What follows is what
was true on 2026-08-25.)*

**Azure OpenAI has never run against the live service.** There is no endpoint and no key on this
machine, and there is no honest way to test it from here. Objective mode degrades to the keyword
fallback when no model is configured, and that is the path every test exercises. Whoever configures
a real deployment runs that code for the first time.

**Plugin isolation stops at the process.** ADR 0048 states the boundary exactly: isolated from
Aurora — process, database, vault, environment — and **not from the machine**, because the network
and the filesystem stay open to the same OS user. Closing that needs a container, a jail, seccomp or
App Sandbox: per-platform work, and work I could not verify on platforms this machine is not.

Both are named here rather than in a list somebody has to reconstruct.
