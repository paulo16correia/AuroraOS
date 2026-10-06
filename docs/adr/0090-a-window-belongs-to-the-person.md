# Design 0090 — A window belongs to the person

**Status:** Implemented · **Date:** 2026-10-06
**Rests on:** `docs/adr/0073` (one voice), `docs/adr/0074` (the conversation window), LAW-002

## What was broken

Aurora sat in a Discord voice channel, heard somebody say *"Aurora, que horas são?"*, decided she had
been addressed, checked that a conversation window was open, asked the model, got a sentence — and
stopped. Every sentence she had to say was refused with `requires_approval`, three times in four
minutes, while a consent window with fifty unspent answers sat active in the database.

The window and the answer belonged to different principals:

```
window opened by   principal: local-mcp-client   (the operator approving converse)
reply executed by  principal: voice              (Aurora answering)
```

`TryUseAsync` matched `principal_client_id`, so a window the operator granted could only ever be
spent by the operator. The only thing that speaks in a voice conversation is Aurora. The window could
never be spent by anything.

That makes `discord.voice.converse` impossible to use for the one thing it exists for. Its own
reasoning, from `docs/adr/0074`: *approving every sentence is not a conversation — nobody sits at a
keyboard clicking yes while their friends talk.* It was approved three times that evening and never
covered a single sentence.

## The decision

A consent window belongs to the **person** who granted it, not to the interface they granted it
through. `TryUseAsync` and the live-session lookup match `principal_os_user`.

Which of Aurora's faces asked for the window — the MCP client, the control panel, the voice
boundary — is a fact about how somebody said it, not about what they allowed. The client id is still
recorded, because the record should say which face asked; it is no longer what decides who may spend
it.

## Why this is not a loosening

The clause this replaces checked the client id and **never looked at the person at all**. So this
adds a constraint that was missing: a window one person granted cannot be spent by another, which
was previously possible for any two callers sharing a client id. A test holds it.

What a window authorises is unchanged, and that is where the safety lives:

- **The named actions.** A window covers what it named and nothing else — a window over
  `discord.voice.reply` does not cover `discord.messages.send`, and a test holds that too.
- **A person approved the call that opened it.** The manifest reader refuses `opens_window_for` on a
  capability that is not itself `approval_required`: *a window is authority a person grants, so the
  call that opens one has to be a call they approved.*
- **Bounded in time and count**, both declared in the manifest and capped by the reader at one hour
  and two hundred actions.
- **Dies with the boot and the policy version.** A restart or a policy change ends it.
- **Never above MEDIUM**, and never covering an action that did not already require approval.

So the authority is the set of named actions a person approved, for a bounded time. Requiring that
the same *interface* spend it was not protecting any of that. It was an accident of how the window
was looked up, and its only effect was to make the mechanism unusable.

## What this does not do

It does not scope a window to one conversation. A window over `discord.voice.reply` covers answering
in whatever voice conversation is open while it lasts, which is what somebody approving "talk with
them for fifteen minutes" means — but if Aurora were ever in two channels at once, one approval would
cover both. The Discord plugin holds one voice session at a time, so the question does not arise
today; when it does, the window needs a scope and this record is where to start.

It also does not change who may *open* one. That is still a capability a person approves, by name, in
the words the manifest declares.

## How it was found

Not by a test. By reading the audit after Aurora failed to answer somebody, and comparing the
`principal_client_id` on the window against the one on the refusal. The tests that existed all opened
and spent a window as the same caller, which is the one case that worked.
