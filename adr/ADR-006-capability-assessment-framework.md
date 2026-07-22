# ADR-006 — Non-Execution Capabilities Assessment Framework

**Status:** ACCEPT
**Date:** 2026-07-18
**RFCs affected:** RFC 021, RFC 022, RFC 040, RFC 051, LAW-002, LAW-006

## Context

VS-006 can complete an internal Task, but an email request still only produces a general lack of effect. The Entity must be able to explicitly represent what it lacks without obtaining authorization, tool or access to the external world.

## Decision

VS-007 introduces a persistent `CapabilityRegistry` and auditable `CapabilityRequest`. The initial registration contains active `MEMORY`, `PLANNING` and `INTERNAL_REASONING`; `EMAIL_SEND`, `CALENDAR`, `FILESYSTEM` and `WEB_SEARCH` are defined but disabled.

A Task `INTERNAL_ANALYSIS` that identifies an email request creates `CapabilityRequest(EMAIL_SEND, UNAVAILABLE)`. The Kernel can explain this unavailability to the user. The request is not an Action, it is not authorization and it does not call any provider.

## Consequences

- Aurora distinguishes “I don’t have the capacity” from “I haven’t executed it yet”.
- Trace includes `CAPABILITY_ASSESSMENT`; Events include registration, application and capacity assessment.
- Decision remains `RESPOND`; never passes `TOOL_CALL` in this slice.
- The state of capabilities and requests is preserved in the snapshot for continuity and auditing.

## Migration and rollback

Existing entities receive the compatibility catalog on first VS-007 startup. Disabling this slice prevents new evaluations, but preserves the catalog and persisted requests; There is no tool or authorization to revoke because none has been created.