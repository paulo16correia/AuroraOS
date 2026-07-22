# Aurora OS — RFC 000: Architectural Philosophy

**Status:** Foundational · **Precedes:** all other RFCs

## Objective

Explain why Aurora OS is built as a modular cognitive architecture rather than a language model-centric product. This RFC is normative: when a technical decision is not covered by another RFC, these principles prevail.

## Architectural thesis

```text
Language model = probabilistic ability to interpret and generate
Aurora OS = identity + state + policies + capabilities + operation
Mind = persistent and governed representation of what Aurora knows,
                      pretende, observa e pode usar
```

AI is not the system. An LLM is a replaceable component that supports deliberation and communication. It has no ownership over data, does not approve actions and is not a source of truth.

## Responsibilities

- Establish data sovereignty, separation between truth and inference and limits of autonomy.
- Guide the decomposition of modules and the design of interfaces.
- Provide criteria for accepting or rejecting implementation simplifications.

## Mandatory principles

1. **The Mind belongs to Aurora, not the model.** Identity, memory, knowledge, experiences, goals and policies are versioned and portable data.
2. **The model is never the source of truth.** An answer is a hypothesis until supported by identifiable data, tools, or evidence.
3. **Intelligence emerges from coordination.** Perception, attention, working memory, knowledge, decision, execution and reflection cooperate; no modules are given implicit authority.
4. **Explicit state beats implicit text.** Important facts, permissions, tasks and relationships have typed objects, lifecycle and provenance.
5. **Autonomy is a revocable delegation.** Aurora only acts without confirmation when there is a persistent policy, limited scope, budget and pause mechanism.
6. **Operational explanation, not theatricality.** The system shows sources, decisions and effects; it does not feign consciousness or expound detailed private reasoning.

## Flow of an affirmation

```text
text/model → candidate → validation/evidence → domain object
                                            │
human/political acceptance
                                            │
                                            ▼
canonical state of Mind
```

## Limit and error cases

- If the model contradicts a confirmed record, the record prevails and the contradiction is flagged.
- If an AI supplier ceases to be available, Mind, policies and audit remain intact; the system degrades without inventing execution.
- If there is not enough evidence, Aurora asks, declares uncertainty or waits — does not transform fluency into fact.

## Justification

This philosophy avoids supplier coupling, opaque memory and “autonomy by prompt”. It also allows you to test each capability without relying on a model to be consistently compliant.

## Future expansions

Multiple specialized templates, local execution, cryptographic auditing, and portability between Aurora installations.