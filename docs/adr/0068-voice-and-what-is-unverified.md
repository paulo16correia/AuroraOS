# Design 0068 — Voice, and what about it is unverified

**Status:** Partially implemented · **Date:** 2026-08-26

## What voice actually needs

Discord's voice protocol is not an extension of the one the rest of this integration uses. It is a
second websocket, a UDP flow, an AEAD cipher, and a codec:

| | |
| --- | --- |
| Voice gateway v4 | a second websocket, its own handshake and heartbeat |
| UDP + IP discovery | a 74-byte probe to learn the external address Discord will send to |
| `aead_xchacha20_poly1305_rtpsize` | Discord retired the older modes; not in Python's standard library |
| **Opus** | mandatory. Discord carries Opus and nothing else |
| Speech-to-text | to turn what people say into something Aurora can think about |
| Text-to-speech | to say anything back |

Opus is the one that settles it. A codec cannot be implemented in a plugin, and it is not optional
— there is no PCM path in Discord's voice protocol. So voice has a **native dependency**, and no
amount of care in this repository removes it.

That is stated first because everything below is shaped by it.

## What is verified

**The turn-taking rules.** `voice_session.py` is a state machine with no I/O: it takes events and
returns decisions, and 13 tests exercise it. This is where being wrong is expensive, and it is the
part that has nothing to do with codecs.

- Aurora's own audio is never input. Without that check the system transcribes itself and answers
  its own sentences, and a system that answers itself does not stop.
- Somebody starting to speak stops Aurora mid-sentence. A system that finishes its sentence while
  being interrupted is not in a conversation, it is broadcasting.
- Audio from a stream nobody has claimed is discarded rather than attributed. An observation naming
  the wrong speaker is worse than one that never happened.
- Silence ends a turn, one turn produces one observation, and a 200ms noise is a cough rather than
  a sentence.
- Somebody leaving takes their unfinished turn with them.

**What this machine can do, reported before it is needed.** `discord.voice.status` answers with
what is installed and what is missing, so somebody deciding whether Aurora should join a call finds
out first. Learning that a codec is missing in the middle of a conversation is a bad way to learn
it.

**That audio does not leave.** Every engine is a program already on the machine — whisper.cpp for
recognition, `piper` or macOS's `say` for speech. There is no fallback that reaches a service, and
`audio_leaves_this_machine: false` is asserted by a test rather than promised in a comment. Audio
exists as bytes for as long as it takes to become text, and the temporary file is removed in the
same call that wrote it, including when the engine fails: a crash is not a reason to leave
somebody's voice on the disk.

**That Aurora can always stop.** `leave`, `stop` and `mute` are LOW, declare no effect, and require
no approval — while `join`, `speak`, `listen` and `unmute` all do. Being unable to leave is worse
than leaving unexpectedly: if stopping needed an approval, Aurora could be held in somebody's call
by nobody being at the keyboard. A test asserts both halves.

**That the manifest and the program agree.** A test asks the plugin which capabilities it handles
and compares that to what the manifest declares. A promised capability nothing implements is a
catalogue entry that fails on first use.

## What is UNVERIFIED

**The audio transport.** The voice gateway, the UDP flow, the encryption and the Opus framing are
not implemented, and nothing here pretends they are. `discord.voice.speak` refuses with
`voice_unavailable` when the engines are missing, and reports an **unknown** outcome when the
session exists and the transport does not — because saying "failed" about a transport that may be
half-connected is a guess, and this integration does not guess about external effects.

**Nothing has been run against Discord.** No credentials were available, and a sandbox server was
not created without asking. Every Discord-facing test in this repository runs against a stand-in on
loopback. That stand-in is a real HTTP server and a real websocket — the plugin performs a real RFC
6455 handshake and is disconnected if it masks its frames wrongly — but it is not Discord.

Marked UNVERIFIED in `docs/reference/platform-support.md` rather than described as working.

## Why it was built this way round

The alternative was to write the transport too, unverified, and present a complete-looking voice
stack. That would have been worse than what is here. Unverified cipher and codec glue that
*appears* finished is the kind of code somebody enables in front of other people, and the failure
would land in a live conversation.

What is written is what could be checked. What could not be checked says so.

## To finish it

1. Install the native dependencies: `libopus`, and a local speech-to-text (`whisper.cpp`).
2. Implement `voice_transport.py`: gateway v4, IP discovery, `aead_xchacha20_poly1305_rtpsize`,
   RTP framing at 20ms.
3. Point it at a Discord server created for the purpose, and run the live checks.
4. Move the row in the platform table from UNVERIFIED to VERIFIED — on evidence, not on the code
   existing.
