# Aurora OS — Especificação de Arquitetura

Este diretório é a fonte normativa para a construção da Aurora OS. As RFCs usam as palavras **DEVE**, **NÃO DEVE**, **DEVERIA**, **NÃO DEVERIA** e **PODE** no sentido obrigatório, proibido, recomendado, desaconselhado e opcional.

## Ordem de leitura e dependências

```text
00 Visão → 01 Princípios → 02 Núcleo cognitivo
                                │
                ┌───────────────┼────────────────┐
                ▼               ▼                ▼
           03 Memória     05 Planeador      07 Personalidade
                │               │                │
                ▼               ▼                ▼
           04 Grafo       06 Ferramentas    08 Aprendizagem
                └───────────────┬────────────────┘
                                ▼
                  09 Segurança → 10 API → 11 UI → 12 Operação
                                                    │
                                                    ▼
                                               13 Roadmap
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
| 00 | Visão | — |
| 01 | Princípios e governação | 00 |
| 02 | Núcleo cognitivo | 00–01 |
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

