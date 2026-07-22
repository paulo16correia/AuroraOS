# ADR-000 — Freeze v1.0 and change control

**Status:** ACCEPT
**Date:** 2026-07-17
**RFCs affected:** all RFCs v1.0

## Context

The architecture already contains Kernel, Mind, Applications, Constitution, Laws, cognitive cycle, persistent state, Event Bus and governance. Continuing to add concepts would increase the risk of overlap and postpone empirical validation.

## Decision

Adopt Architecture Freeze v1.0. The baseline is implemented and tested in a defined order; structural changes undergo ADR. RFCs 021–025 are the normative source of the cognitive cycle; RFC 02 remains the implementation orchestration reference, without introducing competing semantics.

## Consequences

- Implementation starts with contracts and Kernel infrastructure, not connectors or UX.
- Discoveries undergo review/ADR; there are no informal architectural changes.
- Documentation of implementation, tests and standards becomes the priority.

## Migration and rollback

There is no data migration. Revoking this ADR requires subsequent ADR acceptance and a new full baseline review.