# Aurora OS — RFC 030: Signal System

**Status:** Normative · **Depends on:** RFC 011, 021, 040, LAW-001

## Objective

Represent stimuli from the world that may deserve attention. A **Signal** is not an event: an event is a standardized and immutable fact; a signal is a temporary assessment of relevance, urgency, and need for disruption derived from that event.

## Architecture

```text
External World → Event/Observation → Perception → Signal
                                               │
urgency/priority ──┼→ Attention → Cognitive Cycle
└→ queue, suppression or escalation
```

## Data structures

```text
Signal
  id, source_event_ref, kind: MESSAGE|ALERT|CHANGE|SCHEDULE|HEALTH|OPPORTUNITY
  severity: INFO|LOW|MEDIUM|HIGH|CRITICAL
  urgency, relevance, confidence, target_refs[], expires_at
  interruptibility: QUEUE|FOCUS_WHEN_IDLE|INTERRUPT|EMERGENCY
  status: NEW|QUEUED|FOCUSED|SUPPRESSED|EXPIRED|RESOLVED
  reason_codes[], policy_refs[]
```

## Interfaces

```text
Signals.emit(event_ref, classifier) -> Signal
Signals.route(signal_id) -> RouteDecision
Signals.acknowledge(signal_id, resolution_ref) -> Signal
```

## Mandatory rules

1. Every Signal must reference an observable source; the model does not create signals without a verifiable event, schedule or health state.
2. Urgency does not grant permission or call for tools; it only changes attention and order of evaluation.
3. Interruption requires a policy threshold and must preserve the interrupted cycle for recovery.
4. Signals expire or are resolved; do not become permanent memory without LAW-001.

## Limit and error cases

- **Server down:** signal `CRITICAL/EMERGENCY` stops low-risk work, but all corrective action continues to require decision and permission.
- **Non-urgent message:** goes to `QUEUE` or `FOCUS_WHEN_IDLE`; does not trigger automatic action.
- **Signal storm:** deduplicate by source/target, group and apply rate limits.

## Justification

Signals transform events in a nervous system with explicit priority, without confusing external noise with an obligation to act.

## Future expansions

Incident correlation, multimodal signals, outage policies by channel, and alert fatigue analysis.