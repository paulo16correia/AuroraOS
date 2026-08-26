# Design 0060 — `files.organise_sandbox`, the reference capability

**Status:** Implemented · **Date:** 2026-08-26
**Changes:** `CapabilityDescriptor.Reversible`, `AllowlistPolicyEngine` v2

## Why a reference capability

The six capabilities Aurora shipped with are each small enough to be read in one screen, which was
the point while the kernel around them was being built. None of them shows what a *serious* one
looks like — one whose input is a structure rather than a string, whose effect is worth reviewing
before it happens, and which can fail halfway.

This is that worked example. It moves files inside the sandbox into folders by rule.

## The five things it demonstrates

**A plan is a separate thing from an effect.** `dry_run` returns exactly what would happen and
changes nothing. RFC 01 rule 1's ladder — observe, suggest, prepare draft, execute with approval —
expressed inside one capability rather than across two, so the draft and the act cannot drift apart.

**All of it or none of it.** The whole plan is built and checked before the first file moves, and a
move that fails partway undoes the ones before it. A half-organised sandbox is worse than an
unorganised one, because nobody knows which half. The undo is best-effort and says so: the failure
message names how many moves were undone out of how many were made, rather than implying the sandbox
was restored.

**Reversible in fact, not in claim.** The result carries the inverse plan, in reverse order, so
replaying it undoes the run exactly. The test replays it rather than checking the field is present.

**Idempotent.** Running it twice moves nothing the second time, and says why: a file already where a
rule wants it is reported as `already_placed` rather than moved onto itself.

**Ambiguity is refused, not resolved.** A file matched by two rules stops the run — picking the first
would make the outcome depend on the order the rules were written in, which is not a decision
anybody made. Two files that would land on the same name are caught while planning, when nothing
has moved yet, rather than by the mover halfway through.

## What HIGH had to mean before this could run

The capability is HIGH: writing one named file is a thing the owner pictured when they approved it;
rearranging a directory by rule is not, and the gap between what a rule says and what it matches is
where the surprise lives.

The policy engine denied every HIGH capability outright, so declaring it HIGH made it dead. The
tempting fix was to call it MEDIUM. The honest one was to decide what HIGH means:

> **HIGH is permitted only when it is approval-gated *and* declares itself reversible.**

Approval is a person saying yes once. At HIGH that is not enough on its own — if it goes wrong,
somebody has to be able to put it back, and a capability that cannot say how is not one a default
policy should permit. `CapabilityDescriptor.Reversible` is that claim, it defaults to `false` because
the honest answer for an author who did not think about it is that they did not, and Article 3 of the
Constitution already asks the same question of the decision.

Policy version is now `allowlist-v2`, so consent sessions issued under the old rules stop matching.

## A bug the tests found

`**/*.md` did not match a file at the root. Substituting `**` for `.*` leaves the following slash
behind and quietly requires at least one folder, where every tool that uses this syntax treats
`**/` as *zero* or more. The compiler is now a walk over the glob rather than a chain of
replacements, and `**/` becomes `(?:.*/)?`.

Worth recording because the wrong version passed every test that used a single-star rule, and the
failure only appeared in the one test written specifically to tell the two apart.

## What is verified

Thirteen unit tests against a **real sandbox on disk** — a stub mover that always succeeds would
let every failure path pass without being exercised — and one integration test that runs the whole
thing through the kernel: denied for want of approval, approved, planned, denied again for the real
run because the approval is scoped to that exact input, approved again, moved, and the undo checked.

The listing refuses to descend into a symlinked directory or report a linked file, because a listing
is disclosure: a planted link would otherwise enumerate the owner's home and return it as the
sandbox's contents.

## Running it on this machine

It will not, today, and for the right reason. The disk is at 97%, the resource model reports
CRITICAL, the instance is DEGRADED, and Self refuses anything with an external effect while it is —
"I can prepare, but not send". `clock.now` still answers. That is the design working, and it is why
the end-to-end test runs against a stubbed resource probe rather than the host's real disk.
