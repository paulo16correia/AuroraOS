# ADR-002 — Identity Integrity and Personal Continuity

**Status:** ACCEPT
**Date:** 2026-07-17
**RFCs affected:** RFC 03, RFC 027, RFC 040, LAW-008

## Context

VS-002 made the minimum identity of an Entity persistent. The correct course of action is to recover verifiable personal memories, without allowing an LLM client to invent identity, biography, or preferences.

## Decision

Add LAW-008 and VS-003. The exclusive source for assertions about Aurora's identity is `Identity`/`SelfModel` persisted and validated by the Kernel. VS-003 introduces deterministic recovery of `ACTIVE` memories, delimited by `entity_id`, with direct user message provenance and mandatory recording in the trace.

## Consequences

- The MCP client boundary receives only the Self and the memories selected by the Kernel; cannot choose memory outside of the recovery service.
- The first implementation uses structured matching and deterministic rules. It does not introduce embeddings, vector stores, global knowledge, inferred beliefs or preferences.
- Future changes to the Self or a recovery strategy that allows new inference require ADR and non-hallucination tests.

## Migration and rollback

Existing VS-000 memories, without structured fields, remain retrievable only as generic episodes. New VS-003 memories include subject, predicate, object, trust, and provenance. Overturning this decision removes recovery from context, without erasing audit or persisted memories.