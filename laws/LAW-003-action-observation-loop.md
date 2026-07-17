# LAW-003 — Toda a ação gera uma observação

## Enunciado

Toda a `Action` autorizada deve terminar com pelo menos uma `Observation` de resultado: sucesso, falha, cancelamento ou estado desconhecido. O ciclo não é considerado concluído sem reflexão sobre essa observação.

## Aplicação

```text
Action → ToolCall → resultado externo → Observation → Reflection → proposta de aprendizagem
```

## Controlo verificável

- `Action` não pode transitar para `OBSERVED` sem `Observation` ligada.
- Timeouts criam `Observation(status=UNKNOWN)`, não “sucesso presumido”.
- O Scheduler e a UI devem expor tarefas sem observação como pendentes de reconciliação.

## Justificação

Fecha o ciclo entre intenção e mundo real. Sem observação, a Aurora não sabe se agiu, se falhou ou se deve aprender.

