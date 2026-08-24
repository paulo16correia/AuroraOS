# Design 0025 — Capability Resolver (step 8a)

**Status:** Implemented · **Date:** 2026-08-24
**Implements:** RFC 051 · **Gate:** steps 8–9 — *capability does not couple to provider*

## The intention is not the supplier

RFC 051 exists so that "I need to communicate" is not the same sentence as "call Gmail". The Mind
states a capability; the Kernel picks a permitted realisation. That indirection is what stops every
objective becoming welded to whichever provider happened to be configured first.

## Rule 3 is checked twice

A provider whose declared effects exceed the capability's manifest is refused **at registration**,
with the excess effects named. It is checked **again at resolution**, because a manifest can be
edited after a provider was registered against it, and a stale registration is exactly how an effect
nobody sanctioned slips into a capability.

## Rule 2: what resolution weighs

Permissions, availability, declared effects, data classes, target constraints and cost — in that
order, because permission is not a tie-breaker.

**A preference orders; it does not override.** A preferred provider wins among those already
eligible, and a test asserts that a preference over the cost ceiling loses to a cheaper alternative.
RFC 051's own limit case says it: preference influences the choice but does not exceed security or
cost.

## Rule 4 and the substitution that must not happen

This is the rule with the sharpest edge, and the RFC's example states it exactly: *email unavailable,
Discord available — does not automatically replace if the intention was to send email to a specific
recipient.*

A request carries `PinnedProviderId` when the user named a destination or service. A pinned provider
that is unavailable **blocks**; it does not resolve to something else. A pinned provider that fails
mid-flight blocks with a reason saying the request named a specific provider, so no alternative
preserves the intention.

Without a pin, a failure may fall back — but only through the same checks the original passed. The
alternative has to be permitted, affordable and constraint-satisfying in its own right. And when the
only permitted provider is the one that just failed, the answer is blocked rather than a retry loop.

## Blocked names what is missing

No provider produces `BLOCKED` with the capability id in the reason. It never degrades into a
generic shell call — the limit case the RFC calls out, and the failure that would quietly undo the
entire abstraction.

Resolving an unregistered capability is refused outright, so `shell.execute_anything` is not a
capability that merely lacks a provider; it is not a capability at all.

## Every resolution explains itself

`ExplainResolutionAsync` returns a verdict per provider — chosen, unavailable, missing permission,
effects beyond the manifest, constraint unmet, over cost ceiling, not the pinned provider, or lower
priority. Recording only the winner would make "why not the other one?" unanswerable, which is the
question asked whenever a resolution surprises somebody.

## Tests

15 conformance tests: a capability resolving without naming a supplier; a provider exceeding the
manifest refused; missing permission blocking; preference ordering and preference losing to the cost
ceiling; unmet constraints and forbidden data classes excluding providers; no provider blocking with
the capability named; a pinned provider never substituted, either when unavailable or after failing;
an unpinned failure falling back to an equally permitted route; a failure with no alternative
blocking; every provider getting a verdict; a blocked resolution explaining itself; and an
unregistered capability refused.

## Deferred

RFC 051's future expansions: capability composition, quality negotiation, a certified marketplace
and declarative fallback. Health references are carried and not yet consulted — availability is a
flag, not a probe.

## Next

Step 8b: the Tool Manager — manifests, the `ToolCall` lifecycle including `UNKNOWN` and reconcile,
untrusted output validation, and secret handles rather than secret values.
