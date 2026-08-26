# Design 0066 — Capabilities people can write safely

**Status:** Implemented · **Date:** 2026-08-26

## The descriptor was a security control nobody checked

Adding a capability was already two steps: write a class implementing `ICapability`, add one line
to `ServiceRegistration`. Policy needs no registration, because `AllowlistPolicyEngine` decides
entirely from the descriptor the capability carries.

That is good design and it had a hole in it. The engine allows outright anything declaring
`Risk.Low`, no effects and no approval — no consent path, nobody asked. Nothing verified the
claim. A capability that wrote to the sandbox and described itself as effect-free would have run on
every call, and the only thing standing between that and a release was somebody noticing in review.

Now the ports say what they do. A method that changes something carries `[Effect("files.write")]`,
and `CapabilityDeclarationTests` fails any capability that calls one without declaring it. The
attribute goes on the method rather than the interface because `INoteStore` both reads and writes;
marking the interface would fail honest read-only capabilities and teach people to work around the
check.

Two more shapes are caught while they are cheap to fix: a `High` capability without
`Reversible: true`, which policy denies on every call and which would otherwise be discovered after
it was written, and any schema that forgets `additionalProperties: false`.

The check was verified by breaking something. `files.write_sandbox` re-declared as `Low`/no
effects/no approval fails both tests with the reason spelled out.

## A bug the check uncovered

`BuiltInCapabilityIds` feeds the guard stopping a plugin from claiming one of Aurora's own action
ids. It read each capability's descriptor through `Activator.CreateInstance(type, nonPublic: true)`,
which needs a parameterless constructor — and **only `echo.say` has one**. Every other built-in
threw, was swallowed, and came back null.

So the list contained exactly one entry. A plugin declaring `files.write_sandbox` or
`memory.remember` passed manifest validation.

Not exploitable at runtime, because `CompositeCapabilityRegistry` gives Aurora's own capability
precedence on a collision — the second line that was written for exactly this case in
`docs/adr/0062`. But the guard whose whole purpose was to catch it at validation caught nothing,
and it was invisible because a list that is too short looks the same as a list that is correct.

The descriptor is now read by invoking the constructor with nulls and touching nothing else, the
same way the new test does. Safe here and nowhere else: a capability constructor assigns its fields,
and neither path ever calls `ExecuteAsync`.

## Schemas are built, not typed

Every input schema was a JSON string literal — two hundred characters on one line, no compiler, no
completion, and a mistake invisible until a caller sent valid input and Aurora refused it.

`CapabilityInput` builds them. All seven capabilities were migrated and the suite passed unchanged,
which is the useful evidence: the generated schemas accept and reject exactly what the hand-written
ones did.

Two decisions are taken away from whoever is typing. `additionalProperties` is always false, so a
capability never silently accepts a field it does not understand. `maxLength` on strings and
`maxItems` on arrays are required parameters rather than options, because a field with no ceiling
is a way to put as much as you like into Aurora's database through one call.

## The second thing called a capability

`ICapabilityResolver` and the `capability_definition` table are RFC 051's resolution layer for
choosing between several providers of one capability. Implemented, tested, registered in DI, and
used by nothing on the execution path.

It is left as it is — the dormant-seam pattern from `docs/adr/0059` — but it was silently
confusing: somebody adding a capability would reasonably conclude they had a second place to
register. The guide now says outright that it is not the thing they want.

## The guide

`docs/guides/writing-a-capability.md`, alongside the plugin one. It opens by asking whether the
reader wants a plugin instead, since that is the right answer more often than people expect, and
it spends most of its length on the descriptor, because in Aurora the description is the control.

The five-minute example was compiled verbatim from the file before this was committed.
