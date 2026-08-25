# Design 0048 — The plugin SDK

**Status:** Implemented, with the isolation boundary stated exactly · **Date:** 2026-08-26
**Implements:** `docs/060-plugin-sdk.md`

## A declaration is a limit, never a licence

RFC 060's justification says it in one sentence: a plugin for one thing can be useful **without
gaining powers over email, SSH or the Mind**. Everything here exists to keep a manifest from
becoming an authority.

So every claim in a manifest is enforced as a ceiling. Declaring a network endpoint does not grant
reaching it — it makes reaching anything else a refusal. And an **empty list means none**, not
"unspecified": rule 1 says an undeclared dynamic request is denied, so a field nobody filled in is
enforced as zero rather than as unknown.

Installing grants the **intersection** of what was asked for and what was offered, never the union.
Granting more than was reviewed is how a review stops meaning anything.

## Rule 2, stated exactly

This is the rule most easily claimed and least easily kept, so here is precisely what
`SubprocessPluginHost` does and does not isolate:

| | Isolated? |
| --- | --- |
| **The process** | Yes. Separate address space; a crash is an exit code, not a fault in Aurora. |
| **The database and the vault** | Yes, by construction. The child gets the invocation on stdin and nothing else — no connection string, no key path, no handle. **The environment is not inherited**, so a variable Aurora happens to hold does not travel. |
| **The filesystem** | Partly. A per-plugin working directory and no paths in the environment, but the child runs as the same OS user and can read what that user can read. |
| **The network** | **No.** Nothing stops a child opening a socket. Declared endpoints are a statement Aurora holds the plugin to when *Aurora* makes the call; they are not a firewall. |

Closing the last two needs an OS sandbox — a container, a jail, seccomp, App Sandbox — which is
per-platform work and is not here. **A plugin is isolated from Aurora, not from the machine**, and
that is the summary to make an install decision against. Saying otherwise would be the exact failure
this RFC is written to prevent, one level up.

There is a test that writes a three-line plugin, puts a secret-looking variable in Aurora's own
environment, and checks the child cannot see it.

## Two signatures for two different questions

The **signature** covers identity — plugin, version, publisher — so a manifest can be attributed.
The **integrity hash** covers everything declared, so it cannot be widened afterwards. A test signs
a manifest, then raises its `MaxDataClass`, and watches the hash catch what the signature would not.

An invalid signature is refused before installing and **does not run even in preview**. There is no
mode in which unattributable code executes.

## What sends a plugin to quarantine

- **A new publisher.** A different party behind the same name; the previous decision was not about
  them.
- **New permissions.** A bigger ask than the one that was reviewed.
- **Three failures in a row.** A bad moment is not a broken plugin, and a success clears the run —
  but a plugin that keeps failing stops being asked rather than being retried into the same wall.
- **An output that looks like a credential.** Withheld, not returned and then flagged; a
  `PluginQuarantined` event is published, and the next call is not made until somebody looks.

Releasing a quarantine needs an approval and an actor. It ends because somebody looked and decided,
not because time passed.

## The secret scanner is a heuristic, and says so

It catches bearer tokens, private-key blocks and long values under secret-sounding keys. It will not
catch a determined exfiltration, and the code comment says that rather than implying otherwise.

The structural control is elsewhere and is the one doing the work: **a plugin is never handed a
secret in the first place.** The scanner protects against a plugin that found one another way,
which is a narrower and more honest claim.

## Subscriptions are requests, and the answer is filtered

Rule 4's second half. A manifest's `event_subscriptions` is what a plugin would like; what it
receives is the intersection of that, the events Aurora actually declares, and what its own data
class permits. A plugin declaring PUBLIC receives none of Aurora's PRIVATE events however many it
listed.
