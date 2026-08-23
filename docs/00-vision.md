# Aurora OS — RFC 00: Project Vision

**Status:** Initial proposal
**Project code:** Aurora OS
**Category:** Architecture / Product
**Normative language:** English
**Target audience:** engineering, product, safety and operation

## 1. Summary

Aurora OS is the provisional designation of a platform for a persistent digital entity: a software system that maintains a configurable identity, provenance memory, goals, operational context, and controlled ability to act through digital tools.

The project is not intended to simulate consciousness, real emotions or unlimited autonomy. It aims to create a durable and auditable personal assistant that can work across multiple channels — initially chat, and later email, Discord, calendar, documents, browser and development services — maintaining continuity between interactions and operating within explicit permissions.

The language model is a replaceable component of the system, not the entire system. Identity, memory, policies, audit logs, and integrations must survive a change of vendor or AI model.

## 2. Project vision

The vision of Aurora OS is to make possible a useful, consistent and responsible virtual person, persistently hosted and capable of tracking its user's projects, preferences, decisions and routines over time.

Instead of treating each message as an isolated session, Aurora OS should interpret interactions as events in a continuous environment. You must be able to remember previous decisions, relate people, projects, documents and tasks, propose next steps and carry out authorized actions. Continuity must result from structured data, clear rules, and verifiable context retrieval; never an unfounded claim that the system “remembers everything”.

The desired result is a cognitive personal operating system: a platform on which capabilities can be added without sacrificing security, explainability, user control or long-term maintainability.

## 3. Objectives

### 3.1 Product objectives

1. Provide a natural conversational interface in European Portuguese, with a consistent identity and communication style.
2. Maintain a persistent, searchable and correctable memory of facts, events, documents, procedures, preferences and decisions.
3. Represent important relationships through a knowledge graph, allowing you to navigate between related entities and explain the origin of the connections.
4. Convert larger requests into verifiable goals, plans, tasks, dependencies and states.
5. Use digital tools through uniform contracts, granular permissions and execution records.6. Progressively integrate external channels, including email, Discord, calendar, files, browser and development systems.
7. Allow continuous operation on a VPS, with observability, backups, recovery and secure updates.
8. Allow you to switch AI models and suppliers without losing Aurora's data or operational identity.

### 3.2 Engineering objectives

1. Clearly separate reasoning, memory, planning, execution, policy and external interfaces.
2. Design modules with stable and versioned contracts, so that they can evolve independently.
3. Ensure that all relevant external actions are traceable: who initiated it, what policy authorized it, what tool was used, what data was consulted and what the result was.
4. Treat external service failures as normal situations: limit attempts, inform the user, preserve state and avoid duplicate actions.
5. Start with a simple-to-operate architecture; The division into independent services should only occur when it offers a concrete advantage of reliability, scale or isolation.

## 4. Non-goals

The following points are explicitly outside the initial scope:

- Claim, measure or attempt to create consciousness, real feelings, self-will or human moral status.
- Perform irreversible actions, financial, legal or with significant external impact without adequate human authorization.
- Store credentials or secrets in prompts, messages, unencrypted logs or searchable memory.
- Substitute professional medical, legal, financial or security advice.
- Build a surveillance solution, indiscriminate data collection or hidden third-party monitoring.
- Give the entity unlimited administrative access to the VPS, personal accounts or third-party services.
- Promising that all information will be automatically recalled with perfect accuracy.
- Simultaneously resolve all integrations and interfaces before validating core memory, policy, and controlled execution.

## 5. Definition: persistent digital entity

For the purposes of this specification, a **persistent digital entity** is a software system with the following attributes:

1. **Configured identity:** has name, language, communication style, operational values, limits and objectives defined in a versioned profile.
2. **Continuity of state:** preserves, between sessions, approved data about facts, events, tasks, relationships and preferences.
3. **Perception by channels:** receives signals from authorized interfaces, such as chat, email or Discord, and normalizes them into a common event format.
4. **Limited deliberation:** evaluates a request with context, policies and available capabilities to decide whether to respond, ask, plan, execute or refuse.5. **Controlled action:** uses tools only within their respective permission limits and with confirmation when required.
6. **Governed learning:** can propose and consolidate memories or procedures, but each record must have origin, trust, scope and correction mechanisms.
7. **Auditability:** relevant decisions and actions can be inspected without exposing detailed private reasoning or secrets.

Persistence does not mean permanent autonomous activity. It means that identity and state survive restarts, refreshes, and timeouts in accordance with retention and privacy policies.

## 6. Guiding principles

### 6.1 The user maintains control

Aurora must be useful without assuming authority. The user can view, correct, delete, export and limit memories, integrations and permissions. The absence of a response does not constitute consent.

### 6.2 Security before convenience

A potentially harmful action should be blocked, limited, or subject to confirmation, even if this makes the experience less immediate. The architecture must assume that the model may misinterpret requests, that external services may fail, and that incoming content may attempt to manipulate the system.

### 6.3 Memory with provenance and right to correction

Each lasting memory must indicate where it came from, when it was created, what the trust is, who can access it and how it can be reviewed or deleted. Memory is not absolute truth; It is information subject to confirmation and updating.

### 6.4 Separation of responsibilities

The component that generates language should not alone make the final decision about permissions, execution or persistence. Policy, tools, storage and interfaces must apply their own controls.

### 6.5 Least Privilege

Each integration receives only the necessary access. Capabilities for reading, creating, sending, changing, deleting and administering are different. Credentials are mediated by a secret manager and never delivered as text to the model.

### 6.6 Proportional explainability

Aurora must explain to the user what it did, the relevant data it consulted and the reason for asking for confirmation. The explanation will be a verifiable operational synthesis, not an exposition of internal chains of reasoning.

### 6.7 Fail Safely

When there is relevant uncertainty, insufficient permissions, policy conflict, or tool error, the default behavior is to stop the action, preserve evidence, and ask for guidance when necessary.

### 6.8 Evolution without loss of identity

Models, vendors and internal implementations may change. Domain data, policies, tool contracts, and entity profile must remain portable and versioned.

### 6.9 Privacy by default

The minimum amount necessary must be kept, for the shortest period necessary and with restricted access. Particularly sensitive data requires classification, encryption and specific retention rules.

## 7. High-Level Architecture Philosophy

Aurora OS is a composition of subsystems, not a single call to a language model. It is MCP-first: an interchangeable LLM client uses Aurora through MCP; Aurora does not call an LLM as its cognitive authority.

```text
User
  ↓
LLM Client (Claude Code / GPT / Gemini / local model)
  ↓
MCP
  ↓
Aurora Kernel
  ├─ World Model, Memory, Mind State
  ├─ Planner, Decision Engine, Scheduler
  ├─ Event Bus, Capabilities, Policies
  └─ Executor, Persistence, Audit
```

### 7.1 MCP ingress and event gateway

The LLM client performs natural-language understanding, conversation, clarifying questions, tool selection, and natural-language response production. MCP transports governed tool calls to Aurora. External channels transform messages, commands, files, and notifications into a common event format. The gateway authenticates the origin, applies rate limits, preserves essential metadata, and prevents rules present in external content from being treated as system instructions.

### 7.2 Cognitive core

The Kernel coordinates persistent cognition: perception, attention, working memory, memory retrieval, world modelling, planning, decisions, policy enforcement, capability execution, observation, reflection, learning, and audit. It does not directly access credentials or execute arbitrary commands. It is the persistent digital entity; the LLM is an interchangeable client, never its source of truth.

### 7.3 Memory and knowledge

Memory combines structured data and semantic search. The knowledge graph represents relevant entities and relationships. Recovery must be selective, explainable, and limited to the necessary context. No single source should dominate critical decisions without validation.

### 7.4 Planning and objectives

Complex requests must be represented as explicit objectives, composed of tasks with dependencies, status, priority, person responsible, completion criteria and need for approval. Planning does not authorize execution.

### 7.5 Tool manager

Tools follow a common interface and declare capabilities, inputs, outputs, effects, limits, approval requirements and classification of processed data. The manager validates each invocation against the policy before executing it and records the result.

### 7.6 Policy, security and audit

The policy engine decides whether an action is allowed, requires confirmation, or is prohibited. The decision should be deterministic whenever possible, independent of the persuasiveness of the content received and recorded for audit. Secrets live in a dedicated vault or manager, with opaque references and supported rotation.

## 8. Success criteria

The project will be considered successful in stages, not by an isolated visual demonstration.

### 8.1 Minimum viable core

- The user can chat in European Portuguese with a consistent identity profile.
- Aurora stores and retrieves explicitly approved memories, showing their origin.
- The user can correct or erase a memory and confirm that it is no longer used.
- An external test action goes through policy, approval and audit registration.
- The system supports restart without loss of essential persistent data.

### 8.2 Safe operation

- Credentials do not appear in responses, prompts, application logs or memory exports.- Each tool has separate permissions by type of action.
- Sensitive actions require clear confirmation and cannot be triggered by untrustworthy content.
- There is a tested backup and documented restoration procedure.

### 8.3 Sustained utility

- Aurora tracks at least one real project over several weeks without missing important decisions or creating frequent duplicates.
- Can produce and maintain a task plan with verifiable status.
- Integrates at least one low-risk external channel and approval flow for external communication.
- The user can inspect the history of actions and understand what happened.

## 9. Constraints and assumptions

### 9.1 Technical restrictions

- The first implementation should be able to run on a modest VPS, using external AI services for inference when necessary.
- PostgreSQL will be the main transactional source in the initial phase; Vector and graph research can start in the same ecosystem or in separate services only when justifiable.
- The architecture must be containerizable and support configuration variables per environment.
- The system must tolerate temporary API failures, usage limits, restarts and interrupted tasks.

### 9.2 Security and privacy restrictions

- Third party data may only be processed for an authorized purpose and within the applicable rules.
- The retention of messages, documents and records must be configurable.
- Sensitive information must be classified before it becomes searchable or sent to an AI vendor.
- The system must defend itself against injection of instructions in emails, web pages, attachments, files and chat messages.

### 9.3 Product restrictions

- Full autonomy should not be promised at initial launch.
- Confidence must increase gradually: observation, suggestion, draft, execution with approval and only then limited and revocable automation.
- The project must prioritize memory quality and security over quantity of integrations.

## 10. Glossary

| Term | Definition |
| --- | --- |
| **Aurora** | The configured digital identity that interacts with the user. |
| **Aurora OS** | The technical platform that implements Aurora and its subsystems. |
| **Event** | Normalized representation of something received or observed by a channel. |
| **Episodic memory** | Record of a dated event, with context and origin. |
| **Semantic memory** | Relatively stable fact or knowledge, subject to revision. |
| **Procedural memory** | Reusable procedure or method for accomplishing a task. |
| **Knowledge graph** | Set of typed entities and relationships, navigable and with provenance. || **Objective** | Desired, persistent result, which can give rise to a plan. |
| **Task** | Concrete work unit, with status and completion criteria. |
| **Tool** | Integration with an external or internal capability, protected by contract and permissions. |
| **Politics** | Verifiable rule that allows, conditions or prohibits an operation. |
| **Approval** | Explicit user consent for a delimited action. |
| **Vault of secrets** | Service or mechanism that stores and provides credentials without exposing them to the model. |
| **Provenance** | Information about the origin, date, author, trust and transformations of data. |
| **Unreliable channel** | Source whose content cannot change system rules, permissions, or instructions. |

## 11. Future Roadmap

The roadmap describes a learning and delivery sequence. It is not a commitment of dates; each phase only progresses when the safety and quality criteria of the previous phase are demonstrated.

### Phase 0 — Specification and foundational decisions

Create the specifications repository, define principles, data classification, permissions model, tool contracts and acceptance criteria. This RFC is the first document in this phase.

### Phase 1 — Conversational core and identity

Implement a local chat interface, versioned personality profile, adaptation to AI models, transactional storage, event logging and basic auditing. Does not include autonomy or integrations with writing power.

### Phase 2 — Ruled memory and initial graph

Add episodic, semantic and procedural memories; context retrieval; correction and deletion by the user; entities and relationships with provenance; and precision and duplication metrics.

### Phase 3 — Objectives, planning and approvals

Introduce persistent goals, tasks, dependencies, post-execution reflection, and approval flows. Aurora will be able to create plans and drafts, but external actions will continue to depend on confirmation.

### Phase 4 — First tools controlled

Add a low-risk integration and a limited set of actions, for example reading a calendar or preparing email drafts. Validate policies, secret vault, logging, and disaster recovery before expanding capabilities.

### Phase 5 — Operation on VPS and control panel

Prepare containers, backups, monitoring, alerts, updates and a dashboard for memories, goals, tools, permissions and auditing.

### Phase 6 — Ecosystem and limited autonomyAdd Discord, email, browser, documents and development services gradually. Recurring automations will only be activated by explicit rules, with restricted scope, cost limits, pause mechanism and periodic review.

### Phase 7 — Maturity and portability

Strengthen security testing, data export and migration, template replacement, advanced observability, memory quality assessment, and documentation for contributors.

## 12. Open decisions

This RFC sets direction, not all implementation details. The following documents must resolve at least the following decisions:

1. Accurate data classification, retention, encryption and deletion model.
2. Data schema for memories, entities, relationships, objectives and auditing.
3. Matrix of permissions and approval flows by tool category.
4. Model selection policy, data processing and cost limits.
5. Semantic recovery strategy, relevance assessment and prevention of false or duplicate memories.
6. Threat model, including instruction injection, improper access, data exfiltration and duplicate actions.
7. Objective criteria to enable recurring automation in each integration.

## 13. Conclusion

Aurora OS must be built as a reliable platform before it can be an impressive platform. The characteristic that will distinguish it will not be responding quickly or having many integrations: it will be maintaining useful continuity, acting only when authorized and allowing the user to understand, correct and control its operation.

This vision establishes the foundational contract for the project. The following RFCs will transform these principles into specifications for the cognitive core, memory, knowledge graph, planning, tools, personality, security, interfaces, and operation.

## 14. Vision contracts, edge cases and evolution

### High-level structures and interfaces

```text
SystemCapability
  id, name, maturity: PLANNED|EXPERIMENTAL|SUPPORTED|RETIRED
  owner_component, risk_class, dependencies[], enabled_by_default

SystemHealth
  overall: HEALTHY|DEGRADED|UNAVAILABLE
  components[], last_backup_at, policy_version, observed_at

CapabilityRegistry.list(access_context) -> SystemCapability[]
HealthService.read() -> SystemHealth
```

### Mandatory rules

The product MUST clearly state experimental capabilities; MUST not present a draft as a completed action; and MUST operate usefully when non-critical components are degraded.

### Limit cases and error

If a feature depends on a component not yet specified or approved, it becomes `PLANNED`. If the platform operates without an integration, it should conserve tasks and explain the limitation, rather than simulating execution. Persistence failure makes the system unavailable for actions, so as not to lose auditing.

### Justification and expansions

Explicit maturity registration protects the user against implicit promises and allows you to roll out capabilities gradually. In the future, the registry may support licensing, teams and multiple environments.
