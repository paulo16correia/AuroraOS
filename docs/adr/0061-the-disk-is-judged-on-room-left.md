# Design 0061 — The disk is judged on room left, not on proportion used

**Status:** Implemented · **Date:** 2026-08-26
**Found by:** running Aurora on the owner's machine

## The defect

`SystemResourceProbe` reported the disk as a fraction, and `SystemResourceModel` called anything
above 95% CRITICAL. On a 228 GB disk at 97%, that is **seven gigabytes free** — and Aurora refused
every action with an external effect, because CRITICAL degrades the instance and a degraded instance
will read but not reach outside.

It was right by its own rule and wrong about the machine.

A percentage answers "how much of this disk is spoken for". What Aurora needs to know is whether
there is room to write into, and the two stop agreeing exactly when the disk is large. 3% of 228 GB
is 6.8 GB; 3% of 32 GB is one. Both read as 97%, and only one of them is a problem.

## What replaced it

The probe reports free bytes alongside the fraction, and the disk's status is decided on the bytes:

| Room left | Status |
| --- | --- |
| under 512 MiB | CRITICAL |
| under 2 GiB, or the disk is over 99% full | CONSTRAINED |
| otherwise | NORMAL |

Those floors come from what Aurora writes, not from a round number: the database and its
write-ahead log, an encrypted snapshot of the same, a backup copy beside it, and room left over for
none of those to be the thing that fills the disk.

The fraction still decides one thing. A terabyte at 99.5% has five gigabytes left, which is room —
and it is also a machine filling fast enough that discretionary work should get out of the way.

Where a platform reports no free-space figure the old behaviour stands, because a guess from the
only number available beats treating an unmeasured disk as empty — and beats treating it as healthy,
which is the failure the unmeasured list exists to prevent.

CPU and memory are unchanged. They *are* proportions of a fixed capacity, so a fraction is the right
measure for them; the disk was the one dimension where it was not.

## Health says both figures now

`resources` used to read `CRITICAL, disk 97%`. It now reads `NORMAL, disk 99% used, 2.9 GB free`.
Both, because the status is decided on the second one and a reader given only the first would think
a healthy instance was about to fall over.

## The tests had been saying the wrong thing

Three tests forced a full disk by setting the fraction to 0.99, which no longer means what they
needed it to mean. They now set the free space as well — which is what "the disk filled up" always
meant, and what they should have been saying all along.

## Verified on the machine that found it

With 2.9 GB free: health `PASS` across all seven checks, and `files.organise_sandbox` run end to
end through MCP — denied for want of approval, approved, planned, denied again for the real run
because the approval is scoped to that exact input, approved, and two files moved. The audit log
carries all six steps at `High`.
