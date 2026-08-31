# Design 0070 — A window that names what it covers

**Status:** Implemented · **Date:** 2026-08-31

## Why it came up

Speaking in a voice call worked, and was unusable. Every sentence Aurora said was a write, every
write needed its own approval, and so a conversation was a passphrase dialog per sentence. The
owner asked for a loop and the honest answer was that the consent model did not have one.

The tempting fix was to make speaking cheap — drop the effect, drop the approval, call it low risk.
That is the fix that trades the property for the feature: nothing about saying something out loud
in someone else's call became less consequential because it was inconvenient to approve.

## What the rule was

From design 0001, enforced in `SessionAwareConsentGate`:

> a session never covers a capability that declares effects

Approving a read opened a session; the next read rode it. Approving a write opened nothing. The
reasoning holds up: a session that covered writes would turn one human decision into standing
authority to keep writing, and the person who said yes once was never asked about the rest.

## What it is now

> a session covers a capability with effects only when the session named that capability

The rule is not relaxed. It is made **nameable**. What stays refused throughout — before this
change and after it — is the write nobody named.

A capability may declare, in its own manifest, the window that approving it opens:

```json
"opens_window_for": {
  "actions": ["discord.voice.reply"],
  "max_actions": 50,
  "lifetime_seconds": 3600
}
```

The person approving `discord.voice.converse` is told, in the words of the capability itself, which
repeated action they are consenting to, how many times, and for how long. Then the window pays for
that action — and for nothing else.

## What bounds it

**The manifest is checked before anything is installed.** A window may only name actions declared
in the same manifest, so a plugin cannot mint repeated authority over Aurora's own capabilities or
over another plugin's. It may only name actions that require approval and sit at MEDIUM or below,
because a session has never covered anything above MEDIUM and declaring one that did would be a
promise the gate would not keep. The capability that opens it must itself require approval —
otherwise the authority would be minted by a call nobody was asked about. Both bounds must be
stated: 1–200 calls, 1–3600 seconds. Every one of these is refused at load, where the author reads
the reason, rather than narrowed at runtime where nobody would.

**A window pays only for what it named.** Not for another write at the same risk, and not for reads
either. A window opened for speaking that also covered reading would be a wider grant than the
words a person agreed to. The check is a predicate in the same SQL statement that spends the
budget, so the scope cannot be lost to a race between two calls.

**A window is not reused as an ordinary session.** Opening a nameless session while a named one is
live creates a second session rather than handing back the first, for the same reason.

**Nothing else about a session changed.** Liveness is still a `WHERE` clause: the window dies on
restart, on a policy change, on the kill switch, on its deadline, and when its budget is spent.
There is no sweeper to fail and no timer to trust.

## Two budgets, and which one binds

The manifest's numbers are a ceiling. Inside the plugin, `discord.voice.converse` keeps the window
the owner actually asked for — the minutes and utterances they typed — and refuses a turn past it,
as leaving, muting or stopping does immediately and without permission.

So a conversation is bounded twice, and the tighter bound wins. The ceiling exists because the
Kernel cannot read a plugin's input semantics and should not pretend to: it knows the schema, not
what `minutes` means. The honest limitation is that a two-minute conversation still leaves a
kernel-side window open for its ceiling, and it is the plugin's own guard that stops the turns.
The window covers one named action whose plugin-side gate has closed, which is narrow, bounded and
audited — but it is the plugin's guard doing that work, not the Kernel's.

## Where it is visible

The window appears on the capability card, in the plugin console at install, and in the catalogue
itself. Repeated authority is exactly the thing a person most needs to read before granting it, so
it is named where they decide rather than discovered at the second call.

## What was considered and refused

**Making `discord.voice.reply` effect-free or LOW.** It sends audio to other people. Declaring
otherwise would have made the catalogue lie in order to make the gate agree.

**Letting one approval of a write cover the next ones.** That is the standing authority design 0001
refused, and no amount of it being convenient here makes it a different thing there.

**Deriving the window's bounds from the call's input.** It would fit the owner's numbers exactly,
and it would mean the Kernel reading a plugin's arguments for meaning it cannot verify. A declared
ceiling can be read before installing; an input-derived one can only be read after it is granted.
