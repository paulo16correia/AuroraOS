# ADR-017 — Eventos persistentes desbloqueiam workflows

**Estado:** aceite

Eventos são objetos persistidos pelo Kernel. O Event Engine só transforma tasks `WAITING_FOR_EVENT` em `READY`; não executa capabilities nem decide políticas.
