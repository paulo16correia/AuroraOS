# Security policy

Aurora holds secrets and acts on its owner's behalf. A vulnerability here is not an inconvenience,
so please report one privately and give it room to be fixed before it is public.

## Reporting

Use GitHub's [private vulnerability reporting](https://github.com/paulo16correia/AuroraOS/security/advisories/new)
for anything security-relevant. **Do not open a public issue.**

Useful to include, in rough order of usefulness:

1. What an attacker gets — read a secret, escape the sandbox, make Aurora act without approval.
2. The smallest reproduction you have.
3. The platform, because confinement differs by one (see below).
4. Whether it needs the attacker to already have something: a granted plugin, local access, the
   owner clicking an approval.

You will get an acknowledgement within a few days. This is a small project — that is a statement of
capacity, not of importance.

## What counts

Aurora's threat model is written down, and knowing it will save you reporting something already
accepted. The short version:

**In scope, and taken seriously:**

- Escaping plugin confinement — reading or writing outside the sandbox, reaching a network host
  that was not granted, obtaining the GPU when it was refused.
- Reaching a secret: from a plugin, from a log, from an audit record, from a crash dump, from a
  process listing.
- Acting without the Kernel — any path to an effect that skips policy, consent or audit.
- Making a plugin *request* rather than *report*. The protocol is one-way by design
  ([LAW-002](docs/laws/LAW-002-mind-tool-isolation.md)); a way around that is a serious finding.
- Prompt injection that becomes an **action**. Text reaching Aurora — a message, a document, a
  transcript — is data. If wording inside it causes a capability to run, that is a vulnerability
  rather than a quirk.
- Defeating approval: making an action that requires a human appear approved, or replaying an old
  approval onto a new action.

**Known, accepted, already written down** — please do not report these as new:

- **Linux has no sandbox implementation.** Plugins run unconfined there. Aurora reports this rather
  than pretending otherwise.
- **macOS confinement is unverified.** A `sandbox-exec` profile exists and nobody has proved it.
- **Hard links inside a plugin's own working directory.** Analysed and accepted: planting one needs
  owner privilege on both ends.
- **Speech synthesis leaves the machine.** The sentence Aurora is about to say is sent to a hosted
  service. It is a deliberate trade, reported by `readiness` as `text_leaves_this_machine`, and
  audio recognition remains entirely local.

**Out of scope:** anything requiring the owner's own credentials or physical access to an unlocked
machine. Aurora does not defend the owner against themselves.

## Supported versions

There are no releases yet. The supported version is `main`. When releases exist this section will
say something more useful.

## Disclosure

Tell us first, give us a reasonable window, and we will credit you in the advisory unless you would
rather we did not. If a report goes unanswered for 90 days, publish — an unfixed vulnerability that
nobody knows about protects nobody.
