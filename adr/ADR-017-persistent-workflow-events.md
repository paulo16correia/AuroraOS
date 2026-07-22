# ADR-017 — Persistent events unblock workflows

**Status:** accepted

Events are objects persisted by the Kernel. The Event Engine only transforms `WAITING_FOR_EVENT` tasks into `READY`; it does not execute capabilities or decide policies.