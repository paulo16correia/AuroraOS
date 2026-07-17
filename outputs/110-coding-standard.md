# Aurora Platform — 110: Coding Standard v1.0

**Tipo:** standard de engenharia · **Depende de:** RFC 040, 050, 090, documento 100

## Princípios

Código implementa contratos; não cria semântica. Nomes, tipos, eventos, erros e logs devem permitir encontrar a RFC/ADR que justifica comportamento.

## Regras obrigatórias

- Linguagem de implementação inicial: Python com tipagem estática rigorosa; interfaces e modelos são tipados.
- Estrutura por domínio e camada: `kernel/`, `mind/`, `applications/`, `contracts/`, `adapters/`, `tests/`; nunca por “utils” genérico.
- Eventos usam passado: `MindChangeApplied`, `DecisionCommitted`, `ActionObserved`; comandos usam imperativo: `CreateSnapshot`, `ResolveCapability`.
- APIs e eventos usam `snake_case` em payload; tipos e classes usam `PascalCase`; identificadores têm sufixo `_id`/`_ref`.
- Erros são tipados com `code`, `retryable`, `correlation_id` e mensagem segura. Nunca usar exceção genérica como contrato.
- Logs são estruturados, redigidos e correlacionados; não registam prompts integrais, credenciais ou dados secretos.
- Comentários explicam decisão/limite não óbvio e ligam RFC/ADR; não descrevem literalmente código.
- Interfaces públicas, schemas e migrations são versionados. Alterações incompatíveis exigem ADR/migração.

## Proibições

- Acesso direto entre camadas, `Any` como fuga de contrato, estado global mutável, chamadas de rede em código de domínio, segredos em configuração serializada e retries cegos de ações externas.

## Revisão de código

Cada revisão verifica: responsabilidade única, dono do estado, validação de transição, evento publicado, idempotência, política aplicada, observabilidade, teste e compatibilidade.

