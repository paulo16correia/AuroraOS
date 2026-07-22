# Aurora Platform — RFC 051: Capabilities System

**Status:** Normative · **Depends on:** RFC 06, 045, 050

## Objective

Model functional needs in high-level abstractions. The Mind decides the desired capability — for example, communicating or searching — and the Kernel selects a permitted Application/tool. Therefore, the intention is not linked to Gmail, Discord, browser or any specific provider.

## Architecture

```text
Decision: “preciso comunicar”
             │
             ▼
Capability Resolver
├─ communication → Email | Discord | Telegram | WhatsApp
└─ search → Browser | document index | filesystem
             │
             ▼
Policy + Self + Resource Model → ToolCall concreto
```

## Data structures

```text
Capability
  id, domain: COMMUNICATION|RESEARCH|SCHEDULING|DEVELOPMENT|FILES|AUTOMATION
  intent_schema, effect_classes[], risk_class, required_permissions[]

CapabilityProvider
  id, capability_id, application_id, tool_ref, priority
  availability, cost_model, data_classes[], constraints[], health_ref

CapabilityRequest
  id, decision_ref, capability_id, intent_payload, target_constraints[]
  status: REQUESTED|RESOLVED|BLOCKED|EXECUTED|FAILED
```

## Interfaces

```text
Capabilities.resolve(request, self, policy, resources) -> CapabilityProvider
Capabilities.execute(resolved_request) -> ToolCall
Capabilities.explainResolution(request_id) -> ResolutionReport
```

## Mandatory rules

1. Mind asks for capabilities, not suppliers, unless the user explicitly requires a destination/service.
2. Resolution considers permissions, recipient, classification, cost, availability and explicit preference.
3. A provider cannot offer effects outside of the capability's manifest.
4. Failure of provider allows alternative only if it preserves intention, scope and policy; otherwise it blocks/requests a decision.

## Limit and error cases

- **Email unavailable, Discord available:** does not automatically replace if the intention was to send email to a specific recipient.
- **User prefers VS Code:** preference influences development provider, but does not exceed security/cost.
- **No provider:** `BLOCKED` with missing capability, not a generic shell call.

## Justification

Capabilities provide portability and prevent the model from associating each objective with a fixed supplier. The Kernel decides the best authorized realization.

## Future expansions

Composition of capabilities, quality negotiation, certified marketplace and declarative fallback.