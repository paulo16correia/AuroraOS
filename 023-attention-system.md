# Aurora OS — RFC 023: Sistema de atenção

**Estado:** Normativo · **Depende de:** RFC 03–04, 020–021

## Objetivo

Selecionar um pequeno conjunto de sinais, memórias e objetivos que merece processamento num ciclo. Atenção é gestão de recursos e relevância, não memória e não autorização.

## Arquitetura e fluxo

```text
Evento + objetivos ativos + sinais do scheduler + estado da Mind
                           │
                           ▼
         elegibilidade por ACL/privacidade/recência
                           │
                           ▼
  pontuação: relevância + urgência + novidade + impacto + confiança
                           │
                           ▼
             AttentionSet limitado → Working Memory
```

## Estruturas de dados

```text
AttentionItem
  ref, kind: EVENT|MEMORY|GOAL|OBSERVATION|ALERT
  relevance, urgency, novelty, impact, confidence, recency
  score, reason_codes[], expires_at, sensitivity

AttentionSet
  id, cycle_id, items[], token_budget, item_limit
  status: PROPOSED|LOCKED|RELEASED, selected_at

AttentionPolicy
  id, scope, weights_json, thresholds_json, max_items, version
```

## Interfaces

```text
Attention.rank(candidates, policy, budget) -> AttentionSet
Attention.focus(cycle_id, item_ref) -> AttentionSet
Attention.release(cycle_id) -> void
```

## Regras obrigatórias

1. A atenção DEVE filtrar autorização antes de calcular relevância.
2. O conjunto é limitado por itens, tempo e orçamento; recuperar todo o arquivo é proibido.
3. A razão de seleção e exclusão de itens materiais DEVE ficar registada.
4. Alta saliência não contorna política: um segredo ou instrução hostil não ganha acesso por ser urgente.

## Casos limite e erro

- **300 mil memórias elegíveis:** usar pesquisa híbrida em fases e corte determinístico; não carregar objetos completos antes da seleção.
- **Emergência real:** política pode elevar urgência, mas continua a exigir validação de fonte e ação autorizada.
- **Empate:** preferir evidência mais recente, confirmada e diretamente ligada ao objetivo.
- **Sem candidatos:** produzir conjunto vazio e levar a `ASK`, `WAIT` ou resposta limitada.

## Justificação

Sem atenção, o sistema mistura contexto irrelevante, aumenta custo e cria associações espúrias. Com atenção explicável, é possível justificar porque 17 memórias foram usadas e as restantes não.

## Expansões futuras

Aprendizagem de pesos com avaliação, saliência multimodal, filas de interrupção e atenção por perfil de utilizador.

