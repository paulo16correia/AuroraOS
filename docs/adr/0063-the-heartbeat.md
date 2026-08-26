# Design 0063 — Aurora does its own upkeep

**Status:** Implemented · **Date:** 2026-08-26
**Closes:** the open items in `docs/adr/0062`, the `IEventConsumer` seam in `docs/adr/0059`, and the
dependency cycle recorded at the end of `docs/adr/0056`

## Nothing ran on a timer

There was no background service in Aurora at all. Maintenance ran when something happened to POST
`/v1/maintenance`; the event bus was pumped by nothing. So signals never expired, needs never
decayed, indeterminate tool calls were never reconciled, incidents were never raised, and every
event ever published sat in the outbox undelivered.

That was not visible as a failure, which is what made it survive: each of those components worked,
was tested, and was correct. Nobody called them.

It also made the last open item in `docs/adr/0062` look smaller than it was. "Event subscriptions
are declared and not delivered" is true; the reason was that nothing delivered anything.

## What the heartbeat is allowed to do

Two things, and neither is Aurora acting on its own behalf:

1. **The maintenance pass**, which surfaces and never executes — the property its own tests assert,
   because an upkeep loop that could act on what it found would be the widest bypass in the system.
2. **A pump of the event bus**, which hands facts to consumers that were already subscribed.

Anything with an effect outside Aurora still goes through the kernel, a policy decision and, where
required, a person. **LAW-006 is not softened by there being a clock.**

Each half is isolated from the other, and a failure in either is swallowed after the component it
happened in has recorded it. A loop that dies on one exception never runs again for the life of the
process, and nobody notices, because nobody is waiting for an answer.

`Aurora:HeartbeatSeconds` sets the interval; zero turns it off entirely, which is what tests want —
an instance doing upkeep underneath a test is not a deterministic one.

## Two consumers, and the cycle one of them breaks

**`QuarantineIncidentConsumer`** turns a plugin quarantine into a security incident. This is the
residue `docs/adr/0056` recorded: the plugin registry detects the things worth an incident — an
output shaped like a credential, a manifest that stopped verifying — and could not raise one,
because the incident service disables plugins and the reverse edge would have been a dependency
cycle.

The bus breaks it. The registry publishes a fact; the consumer consumes it. Neither knows about the
other, and what would have been a cycle is a declared event type with a contract.

A secret in a plugin's output opens a `HIGH` incident; three consecutive failures open a `MEDIUM`
one. Only the first is worth revoking the owner's standing consent over.

**`PluginEventConsumer`** delivers events to plugins that subscribed. A classified payload does not
travel: the plugin learns that something happened, not what it was. A plugin that fails a delivery
does not make the event undelivered for the others — retrying the whole fan-out would punish the
plugins that handled it correctly by handing them the same event twice.

Subscriptions are re-declared every beat and are idempotent by id, so a plugin installed since the
last beat is subscribed without anything having to notice. A consumer declares its own event types
on the interface rather than at registration, so the two cannot drift.

## Plugin management is the person's, not the agent's

`GET /v1/plugins` and `POST /v1/plugins/{id}/decide` are behind the operator session, like
approving and forgetting. What is installed is what somebody holding the agent's bearer token would
most want to read before deciding what to attack.

Disabling and releasing are there; **installing is not, and stays on the console**. Disabling only
ever reduces what Aurora will do, and releasing is a person choosing to trust something they had
already installed. Installing from a folder path over HTTP would mean the panel could install
anything the server user can read, which is a different thing entirely.

Releasing requires a reason, because a plugin released without one is a decision nobody can explain
later.

## A bug this found

Containment for an incident naming a plugin looked it up by the whole resource reference —
`plugin/acme/notes` — rather than by the id after the prefix. The registry found nothing and the
incident recorded "not installed": a containment that reported success at doing nothing.

Masked until now because nothing raised a plugin incident, and masked in its own test because the
test used a plugin that genuinely was not installed.

## Verified on the real server

With `HeartbeatSeconds=25`, two `MaintenancePassCompleted` events twenty-five seconds apart, no dead
letters, and the resource check reading `CONSTRAINED, disk 100% used, 1.1 GB free` — which is the
new disk rule from `docs/adr/0061` doing exactly what it should: warn, let discretionary work give
way, and keep working.
