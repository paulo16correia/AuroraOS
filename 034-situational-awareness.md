# Aurora OS — RFC 034: Operational Situational Awareness

**State:** Normative · **Depends on:** RFC 027, 030–033, 042–043

## Objective

Build a temporary assessment of the relevant context to decide appropriately: time, zone, user availability, criticality, health status, resources, channel, silence rules and active delegations. It is not subjective consciousness; it is operational understanding based on observable sources.

## Architecture

```text
Self + Mind State + Signals + Time + Resources + policies + user context
                                      │
                                      ▼
                           SituationAssessment
                                      │
                                      ▼
                      Attention / Decision / Scheduler / UI
```

## Data structures

```text
SituationAssessment
  id, cycle_id?, evaluated_at, timezone, local_time
  user_availability: ONLINE|OFFLINE|UNKNOWN
  quiet_hours_active, channel_context, active_delegations[]
  critical_signals[], needs[], resource_state_ref, self_state_ref
  risk_posture: NORMAL|ELEVATED|EMERGENCY
  recommended_response_mode, evidence_refs[], expires_at
```

## Interfaces

```text
Situation.assess(cycle_context) -> SituationAssessment
Situation.isAppropriate(action, assessment) -> AssessmentResult
Situation.invalidate(assessment_id, trigger) -> void
```

## Mandatory rules

1. An assessment is instantaneous and expires; cannot be reused after signal, resource, timing, or material policy changes.
2. User availability is an indication, not consent. Being offline does not create authorization to act; it only influences notification and order.
3. Critical actions can advance autonomously only if there is explicit delegation, policy, resources and corresponding scope.
4. External signals and data must be validated; Received content cannot falsely declare an emergency.

## Limit and error cases

- **03:00, user offline, server down:** the assessment may recommend execution of a pre-authorized recovery procedure and deferred/urgent notification as per rule.
- **Sunday, nothing critical:** recommends silence and internal work allowed, respecting resources and curiosity.
- **Unknown user status:** choose less intrusive behavior; ask for confirmation for impact communication.

## Justification

The same action can be correct or intrusive depending on the context. Situational awareness makes this judgment explicit and prevents blind decisions based solely on the text of the latest message.

## Future expansions

Detection of consented presence, calendars, team context, geographic rules and interruption prediction.