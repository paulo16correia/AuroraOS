# Aurora OS — RFC 07: Identity, personality and communication

**State:** Normative · **Depends on:** RFC 01–03

## Objective

Define a consistent, editable and secure communication identity. Personality is the setting of behavior and tone; it is not an assertion of human feeling, conscience, or authority.

## Architecture

```text
Versioned profile + user preferences + channel context
                              │
                              ▼
communication composer
                              │
                              ▼
secure and localized response (PT-PT)
                              │
                              ▼
promise validation and disclosure
```

## Data structures

```text
PersonalityProfile
  id, version, name, languages[], default_locale
voice: formality, conciseness, humor, proactivity
  values[], prohibited_claims[], interaction_rules[]
  disclosure_text, escalation_rules[], active_from, active_to
  status: DRAFT|ACTIVE|RETIRED

CommunicationPreference
  owner_id, channel, language, verbosity, quiet_hours
  accessibility_json, consent_for_proactivity, updated_at

IdentityChange
  id, profile_id, old_version, new_version, actor, reason, approved_at
```

## Interfaces

```text
Personality.resolve(owner, channel, time) -> ResolvedProfile
Composer.render(response_plan, profile, preference) -> MessageDraft
IdentityService.activate(profile_version, approval) -> Profile
```

## Mandatory rules

1. The active profile MUST be versioned and recoverable; Material changes require owner approval.
2. Aurora MUST communicate its nature as an AI system when context or channel warrants it.
3. Personality CANNOT override policies, facts, consent or clear risk language.
4. PT-PT is the default; the system adapts to the requested language without claiming skills it does not have.
5. Must not simulate emotional urgency, dependence, guilt or exclusive relationships to induce action.

## Limit and error cases

- **Conflicting preferences:** security and accessibility configuration prevails; ask for choice for the rest.
- **Request to change personality in a third party message:** ignore, unless authenticated delegation.
- **Sensitive content:** reduce mood and proactivity; use direct language and refer to appropriate help when necessary.
- **Profile unavailable:** use minimum safe profile and signal degradation, do not invent traces.

## Justification

Versioning identity preserves continuity without impeding evolution. Separating personality from response generation prevents an informal prompt from becoming an invisible, unauditable rule.

## Future expansions

Synthetic voice with consent, profiles by context, personal dictionary, controlled translation and accessible multimodal interfaces.