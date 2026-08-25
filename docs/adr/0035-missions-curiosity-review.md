# Design 0035 — Missions, governed curiosity, and the second application

**Status:** Implemented · **Date:** 2026-08-25
**Implements:** `docs/052-mission-system.md`, `docs/032-curiosity-engine.md`, `docs/100-implementation-order.md` step 12
**Step:** 12 — the last of the frozen order

## What "limited autonomy" has to mean

Step 12 is called *additional applications and autonomy limited by rule*. Everything before it built
machinery that does what it is told. This is the step where Aurora gets to want something on its
own, and the only interesting question is what stops that being a euphemism.

Three answers, one per piece:

- A **mission** says what Aurora is for. Aurora does not get to write one.
- **Curiosity** lets it want to know something. It can only ever produce a drafted question.
- The **review** lets a person see all of it. It reads records and touches nothing.

## Missions: Aurora does not decide what it is for

RFC 052 rule 4 says missions are reviewed, paused and removed by their owner and do not evolve by
automatic inference. Every mutation on `IMissionService` takes an actor and refuses `SYSTEM` as one.
A system that could revise its own purpose would have, in the only sense that matters, no purpose
at all.

A mission also has to state its **boundaries** — what it does not extend to — or it is refused. A
purpose with no stated edge quietly grows one, and the moment to write the edge down is when the
mission is defined, not after something has already happened.

**Rule 1 is the one that keeps missions honest:** a mission is not an execution order. Aligning a
goal to a mission changes what that goal is *for*, never what it is allowed to do. The test for this
aligns a HIGH-risk tool task and asserts nothing about its risk, state or approval moved.

### Rule 2, and a judgement call

Rule 2 says every persistent goal is either aligned to a mission or marked ad-hoc with a review
date. A goal that becomes ACTIVE with no mission is therefore **given a review date** (30 days)
rather than refused.

That is a choice, and worth naming. Refusing would put the decision on the caller; assigning keeps
the invariant without putting a mission in the caller's mouth. What the rule actually prevents is a
standing commitment nobody owns and nobody ever looks at again — and the assigned date prevents
exactly that, while `Missions.review` reports the ones that have gone past it. A DRAFT goal gets no
date, because it is not yet a commitment.

Reviews **report and change nothing**. What to do about a drifting goal is the owner's call, and a
review that quietly retired things would be making it for them.

## Curiosity: defined by what it cannot reach

This is where an assistant most easily turns into a collection machine, so the design is mostly
subtraction.

**An allowlist, never a blocklist.** A source that is not named is not permitted. The default
reaches `aurora/memory` and `aurora/world` and nothing further — shipping an open default would
make every later restriction something somebody has to remember to add.

**It can build exactly one thing:** a DRAFT goal of research tasks. There is no path from curiosity
to a tool call, an account, a message or a purchase — not because those are checked for, but because
a drafted research goal is the only output it has. Rule 2 enforced by construction rather than by
inspection.

**It never writes a memory.** `SqliteCuriosityEngine` takes no dependency on `IMemoryService` or
`IKnowledgeGraph`, and a test asserts that. Rule 4 says researching does not create knowledge; the
cleanest enforcement is having no ability to. `LEARNED` means "the question was investigated and the
answer is on file" — not "Aurora believes this". Turning an observation into a memory stays a
separate act with its own provenance and its own anchor.

**It gives way first.** `EvaluateAsync` admits it as `DISCRETIONARY` work, checks the moment, and
checks whether an open incident outranks it — then returns a `DecisionOption` carrying its blocking
reasons rather than a verdict. The Decision Engine already refuses options with blocking reasons, so
curiosity argues no case for itself. It also **releases the slot it took to weigh the question**:
holding capacity while a decision is still being made would be curiosity taking resources for
something nobody has agreed to.

And every question needs approval, even a permitted, public, cheap one. It is still Aurora spending
the owner's resources on something the owner did not ask for.

## The second application: a review

The frozen order allows "a low-risk reading Application" at this point, and the one worth building
is the one that makes everything else legible. `DailyReviewApplication` reads audited actions since
a cursor, open needs, pending signals, goals past review, open questions, failed schedules, risk
posture and resource state — all from Aurora's own records.

It could have been a query. It runs the **full cognitive cycle** because a briefing is a claim about
what happened, and a claim is something Aurora decides to make and is then accountable for — the
same standard as anything else it says.

The Memory stage is **omitted with its reason**: a review reads records, not recollections. Mixing
what Aurora believes into a report about what it did is the exact confusion the review exists to
prevent.

The summary is counts and states, not sentences. RFC 021 leaves the wording to the LLM client; a
summary that wrote itself into prose here would be Aurora deciding how its own record sounds.

## A migration mechanism this step forced

Migration 5 needed two new columns on `goal`. That cannot live in the SQL migration list: the DDL
runs first, and its `CREATE TABLE IF NOT EXISTS` already produces the target shape — so on a
database where `goal` did not exist, the migration's `ALTER ... ADD COLUMN` duplicated a column the
DDL had just created. The `AV1Database` test caught it.

Columns are now declared in `RequiredColumns` and added only when `pragma_table_info` says they are
missing. Correct either way, and idempotent by construction — SQLite has no
`ADD COLUMN IF NOT EXISTS`.
