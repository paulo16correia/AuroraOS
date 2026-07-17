# Aurora OS — Especificação de Arquitetura

Este diretório é a fonte normativa para a construção da Aurora OS. As RFCs usam as palavras **DEVE**, **NÃO DEVE**, **DEVERIA**, **NÃO DEVERIA** e **PODE** no sentido obrigatório, proibido, recomendado, desaconselhado e opcional.

## Ordem de leitura e dependências

```text
Fundação normativa
000 Filosofia → 00 Visão → 01 Princípios → 035 Constituição → laws/
                                  │
                                  ▼
                 036 Genome → 037 Desenvolvimento → 038 História → 039 Ciclo de vida
                                  │
                                  ▼
Aurora Platform
├─ Aurora Kernel: 045 Kernel → 050 Event Bus → 051 Capabilities
├─ Aurora Mind: 010 Mapa → 011 Camadas → 020 Mind → 040 Domínio
│  ├─ 021 Ciclo → 022 Decisão → 023 Atenção → 024 Trabalho → 025 Deliberação
│  ├─ 027 Self → 028 Crenças → 029 Relações/Preferências
│  ├─ 030 Sinais → 031 Necessidades → 032 Curiosidade → 033 Recursos → 034 Situação
│  └─ 03 Memória → 04 Grafo → 041 Mundo → 042 Tempo → 043 Mind State
└─ Aurora Applications: 06 Ferramentas → 060 SDK e providers de capacidades

Estratégia e operação
052 Missões → 05 Objetivos/Planos/Tarefas → 07 Personalidade → 08 Aprendizagem
                         │
                         ▼
                  09 Segurança → 10 API → 11 UI → 12 Operação → 13 Roadmap → 090 Review Gate
                                                                    │
                                                                    ▼
                   Freeze v1.0 → 100 Ordem de implementação → 105 Testes → 110 Standards → 120 Done
                                                                    │
                                                                    ▼
                                                   Execution Trace VS-000 → primeiro código Kernel
```

Uma RFC posterior não pode contrariar uma anterior sem criar uma decisão de arquitetura (ADR) que explique a alteração, o impacto na migração e a versão a partir da qual entra em vigor.

## Convenções comuns

- Identificadores são UUIDv7; tempos são UTC em ISO-8601; montantes são inteiros na menor unidade monetária.
- Todos os dados persistidos incluem `id`, `created_at`, `updated_at`, `version` e `tenant_id` quando aplicável.
- Todos os comandos com efeito externo incluem `correlation_id`, `idempotency_key`, autor, política aplicada e resultado.
- `CONFIDENTIAL` e `SECRET` nunca são enviados a um modelo ou conector sem autorização expressa da política.
- A primeira versão é de utilizador único, mas o modelo de dados preserva fronteiras de inquilino para futura evolução.

## Índice

| RFC | Tema | Dependências diretas |
| --- | --- | --- |
| 000 | Filosofia de arquitetura | — |
| 00 | Visão | 000 |
| 01 | Princípios e governação | 00 |
| 035 | Constituição da Aurora | 000–01, Leis |
| Leis | Invariantes de arquitetura | Constituição |
| LAW-007 | Comunicação mediada por eventos | Constituição |
| LAW-008 | Integridade da identidade pelo Self Model | RFC 027, RFC 040 |
| 036 | Genome | 035, 040 |
| 037 | Modelo de desenvolvimento | 07, 027–028, 036, 040 |
| 038 | História de vida | 020, 036–037, 040, 043 |
| 039 | Ciclo de vida da instância | 011, 026–027, 033, 036, 043 |
| 010 | Mapa-mestre da Mind | 000–01 |
| 011 | Camadas cognitivas e fronteiras | 000, 010 |
| 020 | Mind — modelo de estado cognitivo | 000, 01, 010 |
| 021 | Ciclo cognitivo | 020, 040 |
| 022 | Motor de decisão | 01, 021, 040 |
| 023 | Sistema de atenção | 03–04, 020–021 |
| 024 | Memória de trabalho | 03, 021, 023, 040 |
| 025 | Deliberação interna | 021–024 |
| 026 | Scheduler | 01, 021–022, 040 |
| 027 | Self — autoconsciência operacional | 011, 020, 040 |
| 028 | Sistema de crenças e incerteza | 03, 040–041 |
| 029 | Relações e preferências | 04, 028, 040–042 |
| 030 | Sistema de sinais | 011, 021, 040, LAW-001 |
| 031 | Necessidades operacionais | 020, 026, 030, 033, 040 |
| 032 | Curiosidade governada | 028, 030–031, 033–034, LAW-006 |
| 033 | Recursos e energia operacional | 027, 040 |
| 034 | Consciência situacional operacional | 027, 030–033, 042–043 |
| 040 | Modelo de domínio | 020 |
| 041 | World Model | 04, 040 |
| 042 | Modelo temporal e validade | 040–041 |
| 043 | Mind State e continuidade | 020, 024, 027–029, 040–042 |
| 045 | Aurora Kernel | 011, 035, 039–040, 043 |
| 050 | Event Bus | LAW-007, 040, 045 |
| 051 | Sistema de capacidades | 06, 045, 050 |
| 052 | Missões, objetivos e tarefas | 05, 035, 040, 051 |
| 02 | Núcleo cognitivo (orquestrador de implementação) | 00–01; refinado por 021–025 |
| 03 | Memória | 01–02 |
| 04 | Grafo de conhecimento | 03 |
| 05 | Objetivos e planeamento | 02–03 |
| 06 | Sistema de ferramentas | 01–02, 05 |
| 07 | Personalidade e identidade | 01–03 |
| 08 | Aprendizagem e reflexão | 02–07 |
| 09 | Segurança | 01–08 |
| 10 | API e eventos | 02–09 |
| 11 | Interface de utilizador | 10 |
| 12 | Implementação e operação | 09–11 |
| 13 | Roadmap e aceitação | 00–12 |
| 060 | SDK de plugins e extensões | 06, 09, 040 |
| 090 | Architecture Review Gate | 000–060 |
| 100 | Ordem de implementação | Architecture Freeze v1.0 |
| 105 | Estratégia de testes | Leis, 090, 100 |
| 110 | Coding Standard | 040, 050, 090, 100 |
| 120 | Definition of Done | 100, 105, 110 |

## Governação pós-freeze

- [Architecture Freeze v1.0](governance/architecture-freeze-v1.0.md)
- [Architecture Review v1.0](reviews/architecture-review-v1.0.md)
- [Matriz de dependências](reviews/dependency-matrix-v1.0.md)
- [ADRs](adr/README.md)
- [Topologia de repositórios](governance/repository-topology.md)
- [Execution Trace VS-000](execution-traces/VS-000-message-response.md)
- [Execution Trace VS-001 — Resurrection](execution-traces/VS-001-resurrection.md)
- [Execution Trace VS-002 — Entity Birth + Self Model](execution-traces/VS-002-entity-birth-self-model.md)
- [Execution Trace VS-003 — Memory Retrieval + Personal Continuity](execution-traces/VS-003-memory-retrieval-personal-continuity.md)
- [Execution Trace VS-004 — Goals + Controlled Agency Loop](execution-traces/VS-004-goals-controlled-agency.md)
- [Execution Trace VS-005 — Persistent Planning Layer](execution-traces/VS-005-persistent-planning-simulation.md)
