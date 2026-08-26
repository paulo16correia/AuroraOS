# Design 0062 — Plugins people can actually write

**Status:** Implemented · **Date:** 2026-08-26
**Depends on:** `docs/060-plugin-sdk.md`, `docs/adr/0048`, `docs/adr/0052`, `docs/adr/0060`

## What was there

RFC 060's machinery was complete and unreachable from every direction:

- **No way to install anything.** `IPluginRegistry` was registered in DI and exposed by no MCP tool
  and no endpoint. A plugin could be installed only by writing C# inside Aurora.
- **No way to call one if it were.** `StaticCapabilityRegistry` held the compiled-in capabilities
  and nothing else, so a plugin's capability never appeared in `aurora_catalog` and could not be
  reached through `aurora_execute`.
- **No manifest on disk.** `PluginManifest` was a C# record. There was no file an author writes.
- **No documented wire protocol.** The host wrote to stdin and read stdout; nobody outside this
  repository could have known that.

Verification, quarantine, the circuit breaker, the secret-output refusal and the OS sandbox all
worked — on a thing that could not exist.

## The manifest is a file, and the errors are the feature

A plugin is now a folder holding `plugin.json` and a program. `PluginManifestReader` turns the first
into a `PluginManifest` or into a list of problems.

The list is what makes this usable. An author's first encounter with Aurora is this file being
wrong, and "invalid manifest" teaches them nothing — so every check names the field, says what was
expected, and **all of them are reported at once**. Somebody fixing six mistakes should need one
round trip.

That required two passes: the strict deserializer aborts on the first unknown property, so unknown
fields are found by walking the document and the semantic checks run on a lenient parse. It also
names the near misses: `'timout_seconds' is not a field Aurora knows — did you mean
timeout_seconds?`

Two checks exist purely so somebody is told early rather than late:

- **Anything above LOW that did not set `approval_required`** is refused, because policy would deny
  every call to it. It would install and never work.
- **HIGH without `reversible`** is refused, by the same rule Aurora's own capabilities are held to
  (`docs/adr/0060`).

## A plugin's capability is an ordinary capability

`PluginCapabilityBridge` is an `ICapability`, and the point of it is that there is nothing special
about it: same catalogue, same policy engine, same persisted approval, same cognitive cycle, same
audit log. A separate path for plugins is how a system ends up with two sets of rules and only
remembers to update one.

It calls through `IPluginRegistry` rather than the host, because the registry is where the
permission check, the classification ceiling, the circuit breaker and the secret-shaped-output
refusal live.

Aurora's own capabilities win a collision, and the manifest reader refuses a key that already
exists — twice, because a plugin silently shadowing `files.write_sandbox` would be the most valuable
bug in the system to whoever wrote the plugin.

**The catalogue is read once, at startup.** A catalogue that changed shape underneath a call already
in flight would mean a capability could be resolved, approved, and be a different capability by the
time it ran. Installing therefore takes effect on restart, which the console says out loud.

## Signing: the owner is the trust anchor

There is no publisher signature and that is a decision. Aurora runs on one machine with no network,
so nothing about a publisher's identity is verifiable here; a signature from one would be a ceremony
proving nothing.

What the owner does is read the manifest, say yes, and Aurora seals exactly that with its own key.
Re-verification on every call proves nothing has changed since — which is the property that actually
matters, and it is the one that catches a plugin edited after approval.

## The bug this found

The scaffolded plugin did not run. `SubprocessPluginHost` cleared the child's environment
completely, so `#!/usr/bin/env python3` could not find an interpreter and every plugin written the
ordinary way failed with exit 127.

The child now gets a **fixed** `PATH` naming only system directories. The property that matters is
that nothing of Aurora's travels, and a constant carries nothing — clearing the environment was
protecting the letter of that at the cost of the thing being usable at all.

Found by scaffolding a plugin and running it, not by a test. A test would have supplied its own
executable path and never touched a shebang.

## Verified end to end

`plugin new` → `plugin validate` → `plugin install` → restart → the capability appears in
`aurora_catalog` as `hello.greet` with the publisher named in its description, and
`aurora_execute` returns `{"greeting": "Hello, Paulo."}` through the full kernel path.

## Still open

**Installing is console-only.** Deliberately: it grants third-party code a place in Aurora's
catalogue, and an endpoint that did it would let whoever holds the agent's bearer token extend
Aurora, which is the one thing that token is not for.

**Event subscriptions are declared and not delivered.** `PermittedSubscriptionsAsync` filters them
correctly and nothing pumps events to in-process consumers yet (`docs/adr/0059`). A plugin that
declares subscriptions is not wrong; it is early.
