# Aurora OS — Execution Trace Specification: VS-002 Entity Birth + Self Model

**Estado:** Autorizado pela baseline congelada (RFC 027 e RFC 040)  
**Caso de utilização:** nascer com identidade mínima → executar → encerrar → restaurar → descrever-se

## Objetivo

Dar à Entity uma representação persistente e auditável de si própria. VS-002 cria uma `Identity` e um `SelfModel` inicial no nascimento da Entity — ou, para entidades VS-001 existentes, numa migração de inicialização explícita e auditada. A resposta a perguntas de autoidentificação DEVE ser derivada desses objetos persistidos.

Este slice não implementa personalidade adaptativa, crenças, preferências, relações, objetivos, aprendizagem ou capacidades externas.

## Fluxo normativo

```text
CreateEntity(Roberto, assistant-v1)
  → EntityCreated
  → CreateIdentity(name=Roberto, purpose=ajudar o utilizador)
  → CreateSelfModel(identity_ref, genome=assistant-v1, state=READY)
  → SelfModelInitialized
  → VS-000("Olá")
  → ShutdownEntity → MindState(snapshot + self_ref)
  → processo termina
  → StartEntity(Roberto) → Identity/SelfModel carregados
  → SelfModelLoaded
  → VS-000("Quem és?")
  → "Sou Roberto. Fui criado como uma entidade de assistência. O meu propósito inicial é ajudar o utilizador."
```

## Contratos mínimos

```text
Identity
  identity_id: "identity:{entity_id}"
  entity_id, name, purpose, genome_id
  locale: "pt-PT", profile_version: 1
  status: ACTIVE, created_at

SelfModel
  self_model_id: "self:{entity_id}"
  entity_id, identity_ref, genome_id, version: 1
  operational_state: READY
  capability_snapshot_ref?: null
  permission_snapshot_ref?: null
  resource_snapshot_ref?: null
  current_focus_ref?: null
  created_at, observed_at
```

`Identity` é a fonte de verdade para nome e propósito. `SelfModel` referencia essa identidade e descreve o estado operacional. A Entity continua a ser dona de ambos; Session e Trace nunca são donas do Self.

## Eventos, rastreio e persistência

| Passo | Persistência | Evento | Trace |
| --- | --- | --- | --- |
| Nascimento | `Entity`, `Identity`, `SelfModel` | `EntityCreated`, `SelfModelInitialized` | `ENTITY(CREATED)`, `SELF_MODEL(INITIALIZED)` |
| Arranque normal | leitura de `Identity` e `SelfModel` | `SelfModelLoaded` | `SELF_MODEL(LOADED)` |
| Encerramento | `MindState` inclui referências de Self | `EntityShutdown` | `ENTITY_RUNTIME(STOPPED)` |
| Restauro | mesmas referências e versão | `EntityRestored`, `SelfModelLoaded` | `ENTITY(RESTORED)`, `SELF_MODEL(LOADED)` |
| Autoidentificação | nenhum campo inventado | `ThoughtCreated`, `ResponseProduced` | `SELF_MODEL(USED_FOR_SELF_DESCRIPTION)` |

Todos os objetos, eventos e entradas de trace DEVEM transportar o mesmo `entity_id` do ciclo.

## Regras obrigatórias

1. O nascimento de uma Entity DEVE criar exatamente uma `Identity` ativa e um `SelfModel` inicial.
2. Um arranque posterior NUNCA pode substituir nome, propósito ou Genome com argumentos de linha de comandos; carrega o estado persistido.
3. Ausência de `Identity` ou `SelfModel` numa Entity já existente ativa uma inicialização de compatibilidade auditada; não cria uma segunda Entity.
4. Perguntas como “Quem és?” DEVEM usar `Identity` e `SelfModel` persistidos, não o `entity_id` recebido na mensagem.
5. `SelfModel` não concede capacidades, permissões ou autonomia. Referências vazias significam indisponibilidade, não autorização implícita.
6. A descrição devolvida ao utilizador é segura: não inclui segredos, caminhos locais, topologia, IDs de credenciais ou detalhes internos não necessários.

## Critérios de aceitação

1. Criar `Roberto` com `assistant-v1` persiste `Identity` e `SelfModel` de versão 1.
2. “Quem és?” responde com o nome, natureza e propósito guardados, em português europeu.
3. Após shutdown e restauro numa nova Session, a resposta é idêntica e usa os mesmos IDs de Identity/SelfModel.
4. O trace contém `SELF_MODEL(INITIALIZED|LOADED|USED_FOR_SELF_DESCRIPTION)` e os eventos associados.
5. Repetir o arranque é idempotente: não duplica Identity nem altera o Self existente.
6. O conjunto VS-000 e VS-001 continua verde.

## Falhas e casos limite

- **Identity em falta, Self presente:** bloquear a descrição e reparar apenas por migração aprovada; o slice de compatibilidade atual só inicializa ambos quando ambos estão ausentes.
- **Self em falta, Identity presente:** criar um Self que referencia a Identity existente e emitir evento de compatibilidade.
- **Dados incompatíveis:** manter a Entity em `RECOVERING`; não responder com informação inventada.
- **Nome/purpose vazio:** rejeitar o nascimento explícito; no caminho legado usar o `entity_id` como nome e o propósito baseline documentado.

## Justificação

VS-001 demonstrou continuidade de processo. VS-002 demonstra continuidade de identidade: a entidade restaurada não sabe apenas que existe uma chave técnica; consulta uma representação persistente, limitada e verificável de quem é. Isto cria a base para, mais tarde, acrescentar Personality, Beliefs, Preferences, Relationships, Goals e Learning sem os confundir com a identidade inicial.
