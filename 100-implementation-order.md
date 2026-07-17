# Aurora Platform — 100: Ordem de implementação v1.0

**Tipo:** plano de implementação congelado · **Depende de:** Architecture Freeze v1.0

## Objetivo

Impedir desenvolvimento por saltos entre módulos. Cada etapa cria uma fundação testável para a seguinte; não se começa por Discord, email, browser ou UI antes de existir o Kernel mínimo, estado recuperável e políticas aplicadas.

## Sequência obrigatória

```text
0. Toolchain, repositórios e contratos
1. Kernel mínimo e ciclo de vida
2. State Manager, PostgreSQL e auditoria
3. Event Bus, outbox, idempotência e DLQ
4. Policy/Permission Engine e Vault abstraction
5. Mind Manager + Genome + Mind State/backup/restauro
6. Modelo de domínio + Memory/Knowledge/World Model mínimos
7. Ciclo cognitivo, Attention, Working Memory, Decision e Planner
8. Capability Resolver + Tool Manager sandbox
9. Uma Application piloto de baixo risco e observação end-to-end
10. API e UI de controlo/approvals/auditoria
11. Scheduler, Signals, Needs, situação e manutenção
12. Applications adicionais e autonomia limitada por regra
```

## Gates por etapa

| Etapa | Só avança quando |
| --- | --- |
| 1–3 | lifecycle recupera, eventos são idempotentes e auditoria é verificável. |
| 4–5 | permissões negam por defeito; restauro isolado passa; segredos não aparecem em logs. |
| 6–7 | memória tem proveniência, ciclo não salta Decision/Policy e contexto expira. |
| 8–9 | capability não acopla a provider; cada ação gera Observation e reconcilia `UNKNOWN`. |
| 10–12 | utilizador controla memória/approvals; automações são revogáveis, limitadas e observáveis. |

## Primeira vertical slice

Uma conversa local cria um `Event`, abre ciclo, recupera memória vazia/permitida, produz `Decision(RESPOND)`, guarda auditoria, cria `Observation` da resposta e sobrevive a reinício. Não usa ferramenta externa. Só depois se adiciona uma Application de leitura de baixo risco.

## Regras

- Nenhuma etapa pode introduzir uma dependência de etapa posterior como atalho.
- Descoberta que contrarie RFC/Lei abre ADR e bloqueia apenas o âmbito afetado.
- Cada merge deve manter o sistema a arrancar, migrar e executar o conjunto mínimo de testes.

