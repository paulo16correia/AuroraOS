# Design 0037 — Unfreezing the sandbox file capabilities

**Status:** Implemented · **Date:** 2026-08-25
**Supersedes:** the freeze in `docs/adr/0012`
**Decided by:** the owner

## What changed

`Aurora:SandboxFilesEnabled` now defaults to `true`. `files.read_sandbox` and `files.write_sandbox`
appear in the catalog on a default install.

Not a line of the capabilities themselves changed. They were never broken — they were **early**.

## Why they were frozen, and why that reason is gone

ADR 0012 froze them for one reason: they are step 8 of the frozen implementation order, and they had
been built before steps 3–7 existed. A filesystem capability sitting on top of an event bus, a
policy engine, a memory model and a cognitive cycle that were not yet there is a capability whose
governance is a promise.

That is no longer the situation:

| Then | Now |
| --- | --- |
| No event bus, outbox or DLQ | Step 3, with declared contracts the outbox enforces |
| No policy/consent separation | Steps 4–5, with a persisted approval ledger |
| No cognitive cycle | Step 7, and every MCP call runs through it (ADR 0031) |
| Review conditions 1–5 open | All five demonstrated (ADR 0027, 0016, 0018, 0036) |
| TOCTOU stated as a residual risk | Owner-only enforced, escapes detected and reverted (ADR 0036) |

## What being in the catalog does and does not mean

It means Aurora will *offer* these actions. It does not mean it may take them.

Both are MEDIUM with `ApprovalRequired`, so every single call goes through the persisted approval
ledger: one-time, scoped to that exact input, and re-requested the moment anything about the input
changes. Aurora offering to read a file and Aurora reading one are different events, and only the
first happens without somebody being asked.

On top of that, unchanged: the lexical path check, the link check on every component, the atomic
write, containment re-verified either side of the rename, and a sandbox root Aurora keeps
owner-only. The capability reaches one directory and cannot be talked out of it.

## The switch stays

Turning them off remains a legitimate thing to want — an instance with no business touching files
should not offer to. But the switch is not what makes them safe; it is what makes them **absent**.
Anyone reasoning about the risk should reason about the approval gate, because that is the control
that is doing the work.

## What this does not unfreeze

Nothing else. The sandbox is one directory, text only, no binary writes, no interop, and no path
outside its root. Reaching further — a real connector, a network egress, another user's files — is a
separate decision with its own conditions, and RFC 13 rule 2 still requires a pilot tool to
demonstrate policy, approval, idempotence and reconciliation before external writing advances.

## Taking effect

The default is read at startup. A running instance keeps the behaviour it started with until it is
restarted.
