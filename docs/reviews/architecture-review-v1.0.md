# Aurora Platform — Architecture Review v1.0

**Estado:** ACEITE COM CONDIÇÕES  
**Âmbito:** especificação v1.0 congelada  
**Resultado:** apta para implementação incremental, sujeita aos gates abaixo.

## Método

A revisão avaliou cada domínio contra responsabilidade única, propriedade de estado, máquina de estados, ciclo de vida, eventos, dependências, Leis e recuperabilidade. Não é uma validação de implementação; é a baseline que os repositórios de código devem cumprir.

## Resultado de consistência

| Área | Decisão de revisão |
| --- | --- |
| Mission / Goal / Plan / Task | Mantidos separados: propósito duradouro, resultado mensurável, estratégia revisionada e unidade executável. |
| Memory / Knowledge / World Model / Belief | Mantidos separados: memória é registo com proveniência; conhecimento é afirmação; World Model é projeção temporal relacional; crença é hipótese revisável. |
| Signal / Need / Attention / Situation | Mantidos separados: estímulo, condição operacional, seleção de foco e avaliação temporal de contexto. |
| Self / Mind State / Lifecycle | Mantidos separados: auto-modelo operacional, snapshot serializável e estado macro da instância. |
| RFC 02 / 021–025 | Sobreposição controlada: RFC 02 é coordenação de implementação; RFC 021–025 são normativas para ciclo, decisão, atenção, memória de trabalho e deliberação. |
| Tool / Capability / Application | Mantidos separados: conector concreto, intenção funcional e embalagem/fornecedor de capacidade. |

## Condições obrigatórias antes de implementação de escrita externa

1. Implementar e testar as Leis 001–007 como testes de conformidade.
2. Demonstrar que Kernel não importa nem depende de semântica interna da Mind; só conhece interfaces/versionamento/estado de processo.
3. Demonstrar outbox, idempotência, Dead Letter Queue e reconciliação de `UNKNOWN` no Event Bus.
4. Demonstrar snapshot/restauro isolado antes de qualquer conector com efeitos.
5. Publicar contratos de eventos e matrizes de autorização para cada capability.

## Achados

| ID | Severidade | Achado | Resolução v1.0 |
| --- | --- | --- | --- |
| AR-01 | Média | Risco de tratar RFC 02 e RFC 021 como ciclos diferentes. | ADR-000 fixa precedência normativa. |
| AR-02 | Média | Kernel pode acoplar-se a detalhes da Mind durante implementação. | Matriz de dependências e testes de fronteira obrigatórios. |
| AR-03 | Média | Comunicação direta pode regressar por conveniência. | LAW-007 + outbox/contratos obrigatórios. |
| AR-04 | Baixa | Metáforas biológicas podem ser interpretadas como alegação de consciência. | Documentos usam “operacional”, evidência e estado observável. |

## Decisão

Não foram encontradas falhas que justifiquem novos conceitos. A arquitetura está congelada; as condições devem ser demonstradas na implementação de referência e rastreadas no Definition of Done.

