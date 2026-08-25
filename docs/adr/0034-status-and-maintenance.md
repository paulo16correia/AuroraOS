# Design 0034 — Status and maintenance: capacity, timing, upkeep

**Status:** Implemented, with named gaps · **Date:** 2026-08-25
**Implements:** `docs/033-resource-model.md`, `docs/034-situational-awareness.md`
**Step:** 11c of `docs/100-implementation-order.md`
**Closes:** the steps 10–12 gate — *automations are revocable, limited, and observable*

## Three questions that are not the same question

- **May this happen?** Policy, consent, approval. Decided by the Kernel, unchanged by any of this.
- **Is there room for it?** The resource model.
- **Is this a good moment for it?** Situational awareness.

Keeping them apart is the whole design. ALLOW from admission is not permission. An assessment that
says the moment fits grants nothing. Both can only ever make Aurora **quieter or more careful** —
never freer.

## A missing metric is not a healthy one

`SystemResourceProbe` reads only what means the same thing on macOS, Linux and Windows: process CPU
against core count, GC memory load, and free space on the drive Aurora runs from. Connectivity has
no portable reading that means anything useful — a reachable interface is not a working network — so
it is reported as unknown rather than guessed at.

Everything unreadable comes back as **null, and is named in `Unmeasured`**. A host reporting nothing
is `UNKNOWN`, not `NORMAL`, and admission becomes conservative: discretionary work waits until
something can be measured. RFC 033 asks for exactly this, and the tempting default — treat an
unread number as fine — is how a system becomes least reliable precisely when there is most going on.

The same principle runs through `NeedsSnapshot`, where every field is nullable and null means *not
looked at*. The maintenance pass reports `overdue_goals` and `since_last_backup` as unmeasured
rather than as zero, because reporting "no goals are overdue" when nobody counted is Aurora
inventing good news. Those two are genuine gaps: the Planner's only way to find overdue goals also
*applies an action* to them, so there is no read to call yet.

## What gives way, and in what order

Under pressure, curiosity and indexing and consolidation give way first; then ordinary work; and
what a person or the system's own integrity is waiting on keeps its reserve. Concurrency is capped
with slots **held back** for essential work, so housekeeping can never fill the queue and leave
nothing for what cannot wait.

Everything blocked is **deferred, not denied**. The work is fine; the moment is not, and denying it
would lose it. The one genuine denial is work that will not state a finite cost — an unbounded
estimate is how a budget stops being a budget.

## Received content cannot declare an emergency

RFC 034 rule 4, and the one with real teeth. A message arriving at Aurora and saying URGENT is still
a message: `MESSAGE`-kind signals raise the posture to ELEVATED at most. Only Aurora's own
observations — health, alerts, scheduling — can reach EMERGENCY. Without that split, anyone who can
send Aurora text can put it into crisis mode, and crisis mode is where judgement gets shortest.

**Being offline is not consent.** Not knowing where the person is means choosing the less intrusive
behaviour, never reading the silence as leave to go ahead. Quiet hours change how Aurora reaches
out and not whether it works: internal work continues, imposing on the person does not, and
essential work still gets through.

An assessment **expires after five minutes** and a stale one is refused rather than trusted.
Reusing an old reading of the room is worse than admitting there is no current one.

## The upkeep pass runs unattended, so it is allowed to do almost nothing

`MaintenanceService` expires signals, decays needs, reconciles what a crash left in the air, ticks
the scheduler, and notices what is waiting. It surfaces due runs and detected needs and **runs
neither**. Needs come out `DETECTED`, not `PLANNED` — drafting a goal is a separate act, and running
one is several more.

This is the constraint that matters most in the whole step. A maintenance loop that could act on
what it found would be the widest bypass in the system, precisely because it runs when nobody is
watching.

## Reservations are process-scoped, and that is correct

They are held in memory and reset on restart. Not a shortcut: a reservation stands for work in
flight *in this process*, and a process that died is not still using the CPU. There is nothing for a
restart to reconcile.

## A test-design fault I fixed rather than worked around

The first version sampled the host directly inside `SystemResourceModel`, so its tests passed or
failed depending on how full the test machine's disk was that afternoon — several failed on mine.
Sampling now sits behind `IResourceProbe`. The policy above it — what counts as constrained, what
gives way first — is tested against stated conditions, and the platform reading is the only part
left that cannot be deterministic.
