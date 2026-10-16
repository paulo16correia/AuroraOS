# What needs work

**Written:** 2026-10-16, against `f0102c7`. Every item was read out of the code or the git history,
and each says what the evidence is so it can be checked rather than believed.

Ordered by what it costs to be wrong about, not by effort.

---

## 1. Nothing distinguishes a stopped Aurora from a suppressed one

**Evidence.** No heartbeat, liveness record or expected-cadence marker anywhere in
`src/Aurora.Adapters/Persistence`. `AuroraHeartbeat` is maintenance and decay, not chain liveness.

The hash chain proves what was written was not edited. The anchor proves it was not removed. Neither
says anything about what was never written — so an adversary in control of the process stops the
service, acts, and starts it again, and the chain stays valid and silent. A quiet chain and a
suppressed chain are byte-identical.

**Why it is first.** It is the cheapest of the three audit gaps to close and the largest while open.
A periodic signed liveness record turns "nothing happened" into a claim that can be false.

See `docs/reference/audit-and-vault-threat-model.md`.

## 2. A key compromise has no remediation path

**Evidence.** `IVault.RotateAsync` replaces a stored secret's value. Nothing re-encrypts the vault
under a new master key and nothing re-signs the audit chain under a new HMAC key; `grep -ri rekey`
over `src/` returns nothing.

So if either key is suspected compromised there is no procedure — only a decision about what to
abandon. The vault can at least rotate every secret it holds. The audit chain cannot: the old
segment is verifiable only with the old key.

The shape of the answer exists already. The chain-break seal admits a discontinuity without
fabricating continuity across it, and a sealed re-key marker is the same idea.

## 3. The audit store's serialisation is untested under concurrency

**Evidence.** `grep -c "Parallel\|WhenAll" tests/Aurora.Tests/Unit/AuditStoreTests.cs` → `0`.

`SqliteAuditStore` holds a semaphore per database path for one reason: to keep the chain strictly
linear. The correctness argument for that mechanism has never been exercised under the condition it
exists for. A chain that interleaves is a chain that does not verify, and it would be found in
production rather than here.

## 4. CI has never run

**Evidence.** `.github/workflows/ci.yml` exists; GitHub Actions is not enabled on the repository, so
both jobs were rejected at startup. The badge is commented out honestly (`e30c059`).

**1316 tests pass** is a claim from one development machine. For a project whose thesis is executable
invariants rather than documented ones, an independent runner is the missing piece of
infrastructure — and the Windows job is the only thing that would exercise AppContainer without
somebody carrying the machine to it.

## 5. Speech leaving the machine has no ADR

**Evidence.** `grep -rli elevenlabs docs/adr/` returns nothing. ADRs 0075–0086 cover a backup
Aurora has let go of, time-zone naming, and where experiments live.

It is the largest architectural reversal in the project — `docs/adr/0074` is titled *"A voice that
needs nobody"* and voice now needs a paid account and an internet connection — and it is recorded
only in `d28cc2f` and the voice guide. The reasoning in that commit message is good enough to be an
ADR almost unchanged.

## 6. The local-turn measurement is out of date, possibly in Aurora's favour

**Evidence.** The 694-second turn was measured with three models resident: Ollama ~4 GB, whisper
~1.5 GB, XTTS ~2.5 GB, on 8 GB. XTTS is gone.

That removes about two and a half gigabytes from a machine that was eight gigabytes short of
comfortable, and **nobody has re-measured**. The remaining stack is whisper plus the language model,
around 5.5 GB, which may fit where the three did not. Every stage in that measurement was dominated
by paging rather than compute — Aurora's own parts were 56 milliseconds of the 694 seconds.

**This is the cheapest experiment available and it may answer the hardware question for free.** Do
it before deciding what machine Aurora needs, and before optimising anything.

## 7. Two designs for who turns words into a sentence, one unwired

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

## 8. The keyword guard trusts a capability's own declaration

**Evidence.** `AuroraKernel.cs:151` refuses keyword resolution when `descriptor.Risk != Low ||
descriptor.Effects.Count > 0`. Nothing checks that a descriptor's declared risk and effects match
what the capability does.

So the restriction cannot be forgotten — it is one guard on the resolution path, not a per-capability
flag — but it can be lied to. A capability that declares itself LOW and effect-free while writing
state is reachable by keyword, which is the weakest resolution mode there is.

A test that a capability's declarations are truthful is hard. A reviewer's checklist is not nothing.

## 9. Nothing has met the real speech service, and no microphone has been opened

**Evidence.** `platform-support.md`: ElevenLabs is *"IMPLEMENTED · TESTED against a fake server on
loopback, 17 tests — no key exists here"*. Microphone → speaker with a person talking is UNVERIFIED,
and microphone capture inside the plugin sandbox has never been attempted.

The plugin is tested against a loopback server that can answer slowly, in pieces, or with nothing,
which is worth having and is not verification. Sandbox audio capture is the next thing a real slice
would hit, and `093c8a9` is a warning about that class of problem: a confined plugin that cannot
open its own temporary directory looks exactly like a broken recogniser.

## 10. Two platforms are UNVERIFIED where one is VERIFIED

**Evidence.** `platform-support.md`: Linux confinement (bubblewrap) UNVERIFIED; Windows owner-only
key files (ACL) UNVERIFIED, while macOS mode bits and Linux are VERIFIED.

The Windows ACL one matters more than its row suggests: owner-only key protection is now fail-closed,
so the mechanism that has never been verified on Windows is the one that decides whether Aurora
starts there at all.

---

## Not on this list, and deliberately

**The cognitive layer.** Beliefs, relationships, personality, missions, curiosity, needs and
life-history had **zero changes** between `bf65f1a` and `f0102c7`. The freeze held. Nothing here asks
for it to be lifted, and nothing here asks for any of it to be deleted either — it is implemented,
it is frozen, and it keeps.
