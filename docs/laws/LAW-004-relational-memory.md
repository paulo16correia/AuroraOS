# LAW-004 — Nenhuma memória nasce isolada

## Enunciado

Uma memória persistente deve ter relação com pelo menos uma âncora contextual: entidade, objetivo, conversa, observação, data/intervalo, ferramenta, documento ou outra memória. Registos sem relação suficiente ficam candidatos temporários ou são descartados.

## Aplicação

```text
Memory candidate → âncoras mínimas → MemoryLink/Edge + proveniência → Memory ACTIVE
```

## Controlo verificável

- O validador exige `source_refs` e pelo menos uma âncora tipada.
- `MemoryLink` e relações de grafo guardam razão/evidência, não apenas similaridade vetorial.
- O processo de consolidação reporta candidatos órfãos para revisão ou expiração.

## Justificação

Relações tornam uma memória recuperável, explicável e temporalmente situada. Uma frase solta é fraca demais para orientar decisões futuras.

