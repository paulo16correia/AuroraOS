# Threat model — the audit chain and the vault

**Status:** written against the code, 2026-09-03. Every claim below was read out of
`src/Aurora.Adapters/Persistence` and `src/Aurora.Adapters/Vault` rather than out of an RFC.

This is the most load-bearing part of Aurora. Everything else in the system — the Kernel's
authority, the policy engine, consent, the whole argument that a language model cannot act
unobserved — rests on two claims: that what the audit chain says happened is what happened, and
that a secret in the vault is only readable by something Aurora authorised. This document says
exactly how far those claims reach, and where they stop.

## Three different things, and Aurora only has one of them

| | |
| --- | --- |
| **Tamper-evident** | You can tell afterwards that records were changed or removed. **Aurora has this**, against an adversary who reached the database and not the key. |
| **Tamper-proof** | Records cannot be changed at all. **Aurora does not have this**, and a local-first design on an ordinary filesystem cannot. |
| **Non-repudiation** | The owner cannot later deny what the records say. **Aurora does not have this**, because the owner holds the signing key. |

Selling the audit chain as anything but the first is a lie, and the difference matters most in
exactly the case somebody would want to invoke it.

## What exists

| Artefact | Where | Protection |
| --- | --- | --- |
| Audit records | SQLite, `audit` table | HMAC-SHA-256 chain, each row over its predecessor |
| Audit key | `Aurora:AuditKeyPath` | 32 random bytes in an owner-only file, created on first use |
| Chain head anchor | `<database>.anchor` | Plain text, `sequence hash`, owner-only file |
| Vault secrets | SQLite, encrypted | AES-256-GCM, secret id as associated data |
| Vault key | `Aurora:VaultKeyPath` | 32 random bytes, same file scheme |
| Plugin signing key | `Aurora:PluginKeyPath` | 32 random bytes, same file scheme |

**Three separate key files**, which is right: compromising the plugin key does not open the vault,
and opening the vault does not let anybody forge the audit chain.

`LocalKeyFile` is honest in its own comments about what it is: *"a file on the same disk only
raises the bar. An attacker who can read arbitrary files as this user gets the key and can forge
freely."* This document does not soften that; it works out the consequences.

## The trust boundary

**The process user on the host.** That is the whole boundary.

Everything Aurora protects is protected from something that has less access than Aurora itself: a
copied database, a corrupted file, a plugin in its sandbox, a bug in Aurora, a language model that
was talked into something. Nothing is protected from something with equal access.

## The questions, answered

### Who can obtain the key?

Anything that can read files as the Aurora process user. There is no OS keystore, no DPAPI, no
Keychain, no HSM — deliberately, for portability, and recorded as a future expansion in RFC 09.

Also anything that can read this process's memory: both keys are held as `byte[]` fields for the
lifetime of the process (`AesGcmSecretProtector._key`, `SqliteAuditStore._key`). A core dump
contains them.

### What if the database is copied?

**Audit: safe.** The copy can be read, which is a privacy question and not an integrity one, but it
cannot be extended or edited convincingly without the key. Verification of the copy will fail.

**Vault: safe.** Ciphertext without the key is ciphertext. AES-256-GCM with the secret's own id as
associated data, so blobs cannot be moved between rows either.

This is the case the design actually defends, and it defends it well.

### What if the vault *and* its key are copied?

Every secret, in plaintext, at the attacker's leisure. Two files.

### What about administrator or root access?

Everything. Both keys, both databases, the anchor, and the memory of the running process. There is
no defence here and there is no cryptographic construction that would provide one while the
verifier and the signer are the same machine.

**Against the owner of the host, the audit chain proves nothing.** It is a record the owner keeps
for themselves, and its value is that it catches corruption, bugs, partial compromise and
after-the-fact editing — not that it constrains the person holding the machine.

### Can somebody replace the key and rebuild a chain that verifies?

**Yes.** With write access to the filesystem: generate a new 32-byte key, recompute every record
hash under it, write a matching `<database>.anchor`, and every check Aurora performs will pass.
The anchor's refusal to move backwards is enforced in a running process, not in the file.

This is the sharpest limitation in the design and it follows from the trust boundary rather than
from a mistake. **Tamper-evidence reduces to filesystem access control.** An operator who wants
more must put the anchor somewhere Aurora cannot write — another host, an append-only store, a
notary — and Aurora ships nothing that does this.

### How is rollback detected?

It is not, if the database, the anchor and the key are restored together. Restoring the database
alone is caught: its head no longer matches the anchor. That is a real defence against a careless
restore and no defence at all against a deliberate one.

### How is the absence of events detected?

**It is not.** This is the largest gap in the design, and the anchor does not close it.

A hash chain proves that what is written was not edited. The anchor proves that what was written
was not removed. Neither says anything about what was never written. An adversary with control of
the process does not need to forge anything: they stop the service, act, and start it again. The
chain remains perfectly valid and perfectly silent.

### How do you tell "Aurora was off" from "somebody suppressed events"?

**You cannot.** There is no heartbeat, no liveness record, no expected-cadence marker anywhere in
the persistence layer. A quiet chain and a suppressed chain are byte-identical.

This is the one gap that is cheap to close and currently open, and it is the difference between an
audit trail and an audit trail somebody can switch off.

### What happens after corruption?

`SqliteAuditStore` writes a **chain-break record**: a sealed, signed acknowledgement that history is
discontinuous. Verification finds the newest seal, checks that *its own* signature recomputes under
the current key, and resumes the walk from there.

The check on the seal is the good part: somebody with write access but no key cannot plant one to
excuse their edits. The mechanism admits a discontinuity rather than fabricating continuity across
it, which is the right behaviour and rare.

### How is a key rotated?

**Secrets rotate. Keys do not.**

`IVault.RotateAsync` replaces a stored secret's value, and `RotationOverdueAsync` reports the ones
that are due — a real feature, and not the one this question is about. There is **no path to
re-key** either the vault or the audit chain: nothing re-encrypts the vault under a new master key,
and nothing re-signs the chain under a new HMAC key.

So if either key is suspected compromised there is no procedure. The vault's answer would be to
rotate every secret it holds, which is at least possible. The audit chain has no answer at all: the
old chain can only be verified with the old key, so a re-key either abandons history or requires
keeping the compromised key to read it.

### Is there recovery?

Backups verify. `SqliteBackupService` checks the chain **in the copy** before calling it a backup
and refuses one that does not verify, with the reasoning recorded in the code: *"A copy whose chain
does not verify is not a backup."* Restore carries the anchor with the database.

## What has no defence today, in order

1. **Silence.** Nothing distinguishes a stopped Aurora from a suppressed one. Closable, cheaply: a
   periodic signed liveness record makes a gap visible, and an expected cadence turns "nothing
   happened" into a claim that can be false.
2. **No key rotation.** A compromise today has no remediation path, only a decision about what to
   abandon. Closable, with work: re-encryption for the vault, and for the audit chain a sealed
   re-key marker that keeps the old segment verifiable under the old key.
3. **Keys at rest beside the data.** Raised, not solved, by owner-only ACLs. The interface takes raw
   bytes, so an OS keystore changes one class — the code was written expecting this.
4. **Anchor writable by the thing it audits.** Structural. Only an external anchor fixes it, and
   that ends local-only operation, which is a trade nobody has been asked to make yet.
5. **Keys in process memory for the process lifetime.** Inherent to a long-running signer.

## What this does not cover

Concurrency. `SqliteAuditStore` serialises writes with a semaphore per database path to keep the
chain strictly linear, and `AuditStoreTests` contains no concurrent test of any kind — no
`Parallel`, no `Task.WhenAll`. The correctness argument for the semaphore is untested under the
condition it exists for. That is a testing gap rather than a threat, and it is recorded here
because a chain that interleaves is a chain that does not verify.
