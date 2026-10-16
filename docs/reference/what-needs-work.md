# What needs work

**Written:** 2026-10-16, against `95cc2c7`. Every item was read out of the code or the git history,
and each says what the evidence is so it can be checked rather than believed.

Ordered by what it costs to be wrong about, not by effort.

**First written against a tree 63 commits stale**, which is recorded here rather than tidied away:
two items were already done and are struck out below, and the item now at the top was missed
entirely because the ADR that names it had not been read.

---

## 1. Everybody in a voice channel is recorded and uploaded, and nobody told them

**Evidence.** `docs/adr/0094`, which says it plainly: *"A recording of whoever speaks in the voice
channel is uploaded. Not the owner's voice only — everybody's, including people who joined a channel
and were never told."* `docs/adr/0093` *"named this as the part that did not sit comfortably and left
it unsolved"*, and removing the local recogniser removed the answer that used to exist for anybody
who minded.

The honest record survives — `audio_leaves_this_machine` is `false` only when there is no key, which
means nothing can listen at all, so it *"never means privacy again"*. That is the right field to
have kept and it is not a solution.

**Why it outranks the audit gaps.** Every other item on this list is about Aurora's owner being able
to trust Aurora. This one is about people who are not Aurora's owner, did not install it, were not
asked, and have no way to find out. A governance architecture this careful about the owner's
authority has nothing to say yet about theirs.

Nothing here argues for reversing `0094` — the measurement behind it is sound, and a worse
recogniser that happens to run locally is not a privacy feature. What is missing is the part `0093`
left open: a disclosure somebody in the channel actually receives.

## 2. Nothing distinguishes a stopped Aurora from a suppressed one

**Evidence.** No heartbeat, liveness record or expected-cadence marker anywhere in
`src/Aurora.Adapters/Persistence`. `AuroraHeartbeat` is maintenance and decay, not chain liveness.

The hash chain proves what was written was not edited. The anchor proves it was not removed. Neither
says anything about what was never written — so an adversary in control of the process stops the
service, acts, and starts it again, and the chain stays valid and silent. A quiet chain and a
suppressed chain are byte-identical.

**Why it is first.** It is the cheapest of the three audit gaps to close and the largest while open.
A periodic signed liveness record turns "nothing happened" into a claim that can be false.

See `docs/reference/audit-and-vault-threat-model.md`.

## 3. A key compromise has no remediation path

**Evidence.** `IVault.RotateAsync` replaces a stored secret's value. Nothing re-encrypts the vault
under a new master key and nothing re-signs the audit chain under a new HMAC key; `grep -ri rekey`
over `src/` returns nothing.

So if either key is suspected compromised there is no procedure — only a decision about what to
abandon. The vault can at least rotate every secret it holds. The audit chain cannot: the old
segment is verifiable only with the old key.

The shape of the answer exists already. The chain-break seal admits a discontinuity without
fabricating continuity across it, and a sealed re-key marker is the same idea.

## 4. The audit store's serialisation is untested under concurrency

**Evidence.** `grep -c "Parallel\|WhenAll" tests/Aurora.Tests/Unit/AuditStoreTests.cs` → `0`.

`SqliteAuditStore` holds a semaphore per database path for one reason: to keep the chain strictly
linear. The correctness argument for that mechanism has never been exercised under the condition it
exists for. A chain that interleaves is a chain that does not verify, and it would be found in
production rather than here.

## 5. CI has never run

**Evidence.** `.github/workflows/ci.yml` exists; GitHub Actions is not enabled on the repository, so
both jobs were rejected at startup. The badge is commented out honestly (`e30c059`).

**1316 tests pass** is a claim from one development machine. For a project whose thesis is executable
invariants rather than documented ones, an independent runner is the missing piece of
infrastructure — and the Windows job is the only thing that would exercise AppContainer without
somebody carrying the machine to it.

## ~~6. Speech leaving the machine has no ADR~~ — done

**Wrong when written.** `docs/adr/0087` (the model is reached through the plugin), `0093` (listening
may leave the machine if chosen) and `0094` (one recogniser, and it is not local) all exist, and
`0094` states what it narrows: *"`docs/adr/0045` (local only) for the voice path"*. The reversal is
documented better than I claimed it was not.

## 7. The local-turn measurement is out of date, possibly in Aurora's favour

**Half superseded.** `docs/adr/0091` measured hearing, so the STT half has numbers: `ggml-small` got
a third of the words wrong and `large-v3-turbo` a quarter, at eleven seconds a sentence. What is
still unmeasured is the whole turn with the memory the stack actually uses now.

**Evidence.** The 694-second turn was measured with three models resident: Ollama ~4 GB, whisper
~1.5 GB, XTTS ~2.5 GB, on 8 GB. XTTS is gone.

That removes about two and a half gigabytes from a machine that was eight gigabytes short of
comfortable, and **nobody has re-measured**. The remaining stack is whisper plus the language model,
around 5.5 GB, which may fit where the three did not. Every stage in that measurement was dominated
by paging rather than compute — Aurora's own parts were 56 milliseconds of the 694 seconds.

**This is the cheapest experiment available and it may answer the hardware question for free.** Do
it before deciding what machine Aurora needs, and before optimising anything.

## 8. Two designs for who turns words into a sentence, one unwired

**Evidence.** `plugins/voice/thinking.py` holds an Ollama client and `local_provider.py` drives it —
that is the path that produced the real measured turn. `VoiceConversationBoundary` and
`ILocalLanguageModel` are the C# seam for the same job; the interface has no implementation and
appears in `DormantSurfaceTests.KnownDormant` with the reason *"no local runtime is chosen yet"*,
and `VoiceConversationBoundary` is not registered in `ServiceRegistration`.

The dormancy is handled correctly — the exemption is explicit, named and reasoned, and a new orphan
fails the build. The question is not hygiene, it is direction: two places that can produce a
sentence become two implementations that drift, and the one that gets fixed is whichever somebody is
looking at. That is the argument `7c2c068` already made about the speech client.

Decide which one is Aurora's, and say so.

## 9. The keyword guard trusts a capability's own declaration

**Evidence.** `AuroraKernel.cs:151` refuses keyword resolution when `descriptor.Risk != Low ||
descriptor.Effects.Count > 0`. Nothing checks that a descriptor's declared risk and effects match
what the capability does.

So the restriction cannot be forgotten — it is one guard on the resolution path, not a per-capability
flag — but it can be lied to. A capability that declares itself LOW and effect-free while writing
state is reachable by keyword, which is the weakest resolution mode there is.

A test that a capability's declarations are truthful is hard. A reviewer's checklist is not nothing.

## ~~10. Nothing has met the real speech service~~ — done

**Wrong when written.** It has. `pt-PT` came back as a real `400` — *"Model 'eleven_flash_v2_5' does
not support language_code"* — which is what fixed the locale bug, and `docs/adr/0091` measured
hearing against sixteen known European Portuguese sentences through the production audio path.

What remains true is narrower and still worth a row: no microphone has been opened from inside the
plugin sandbox, and `platform-support.md` still says *"no key exists here"* for ElevenLabs, which
the 400 contradicts.

## 11. One platform is UNVERIFIED, and it is the one that fails closed

**Evidence.** `platform-support.md`: confinement is VERIFIED on all three now, and owner-only key
files are VERIFIED on macOS and Linux. **Windows owner-only key files remain UNVERIFIED — ACL.**

This was written as "two platforms" and Linux had already been verified; the correction is the third
thing on this list that a 63-commit-stale read got wrong.

What remains matters more than its row suggests. Owner-only key protection is fail-closed (F-3,
`docs/adr/0079`): if Aurora cannot establish it, Aurora refuses to start rather than continue with a
key behind a wider permission. So the one mechanism never verified on Windows is the one that
decides whether Aurora starts there.

## Not on this list, and deliberately

**The cognitive layer.** Beliefs, relationships, personality, missions, curiosity, needs and
life-history had **zero changes** between `bf65f1a` and `f0102c7`. The freeze held. Nothing here asks
for it to be lifted, and nothing here asks for any of it to be deleted either — it is implemented,
it is frozen, and it keeps.
