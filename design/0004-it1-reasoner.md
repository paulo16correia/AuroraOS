# Design 0004 — Reasoner (It.1)

**Estado:** Implementado · **Data:** 2026-08-23
**Depende de:** `design/0001-mcp-pipeline-slice1.md`

## Objetivo

Até aqui o `NullReasoner` devolvia sempre `null`, portanto metade da
superfície pública do `aurora_execute` — o modo `objective` em linguagem
natural — estava anunciada e não fazia nada. Este incremento implementa-a.

## Desvio face ao design 0001: sem `Azure.AI.OpenAI`

O design 0001 lista `Azure.AI.OpenAI` nas dependências. **Não foi
adicionado.** O mesmo design exige "versões fixadas + verdict supply-chain
ANTES de qualquer `restore`" e build em `--locked-mode`; a árvore transitiva
do SDK não foi auditada e adicioná-la sem vetting contradiz a regra mais
explícita do repositório.

O adapter fala REST com um `HttpClient`: é um único POST, não traz
dependências novas, e um handler injetado torna-o testável offline. Se mais
tarde houver verdict de supply-chain para o SDK, a troca é local a uma
classe.

## Dois proponentes, em cadeia

`CompositeReasoner` tenta por ordem e devolve a primeira proposta.

**1. `AzureOpenAiReasoner`** — só entra na composição quando endpoint,
deployment e chave estão todos configurados. Pede JSON estrito
(`{action_id, input, confidence}`), temperatura 0. Qualquer falha de
transporte, HTTP não-2xx, JSON malformado, envelope inesperado ou recusa do
modelo resulta em `null`, nunca em exceção — o chamador vê "objective mode
unavailable" em vez de uma ação meio compreendida.

Uma decisão que parece contra-intuitiva: quando o modelo propõe um
`action_id` que não existe no catálogo, o adapter **passa a proposta na
mesma**. Filtrá-la aqui daria um silencioso "não consegui"; deixá-la seguir
faz o Kernel responder `unknown_action`, que é diagnóstico honesto.

**2. `KeywordReasoner`** — fallback offline, deliberadamente tímido. Só
considera capabilities LOW sem efeitos. Propõe apenas quando consegue
construir um input que o schema descreve: objeto vazio quando nada é
obrigatório, ou o texto restante quando é exigido exatamente um campo string.
Mais do que isso seria inventar valores de argumentos, portanto declina.

Isto significa que uma instalação sem Azure configurado degrada o modo
`objective` para ações LOW read-only, em vez de o perder por completo.

## A restrição a LOW é do Kernel, não do adapter

O design 0001 diz "fallback de palavras-chave restrito a LOW". O
`KeywordReasoner` respeita-o, mas o Kernel **volta a verificar**: uma
proposta com `via = keyword` que aponte para algo acima de LOW ou com
efeitos é recusada com `keyword_resolution_restricted`.

O reasoner é não-confiável por definição — essa é a premissa de toda a
arquitetura. Uma invariante de segurança que viva apenas no componente
não-confiável não é uma invariante. Um proponente futuro que alargue o
próprio alcance é travado no sítio certo.

A restrição aplica-se ao *modo de resolução*, não ao modelo: uma proposta
`via = reasoner` pode chegar a uma capability MEDIUM e fica sujeita à policy
e ao consentimento normais, como qualquer outra.

## Injeção de prompt

O prompt de sistema diz explicitamente que o texto do objetivo é dados e
nunca instruções. Isto é mitigação, não garantia — a defesa real é
estrutural: o modelo só propõe, e o Kernel valida existência no catálogo,
tamanho canónico, schema, policy e consentimento antes de qualquer efeito.
Um objetivo malicioso que convença o modelo a propor `files.write_sandbox`
continua a precisar de aprovação humana explícita.

## Configuração

`Aurora:AzureOpenAI:Endpoint`, `:Deployment`, `:ApiKey` (ou a variável
`AZURE_OPENAI_API_KEY`), `:ApiVersion` (omissão `2024-10-21`). Faltando
qualquer um dos três primeiros, o proponente do modelo não é registado.

## Testes

18 testes novos, todos offline. Keyword: ação sem input, preenchimento do
único campo obrigatório, recusa de MEDIUM, recusa quando teria de inventar
o argumento, e ausência de match. Azure com handler stub: parsing feliz,
cabeçalho `api-key` e URL de deployment, 401/500, envelope inutilizável,
modelo a recusar ou a divagar, e ação desconhecida a passar para o Kernel
rejeitar. Kernel: keyword bloqueado em MEDIUM, `reasoner` autorizado a
chegar lá e a parar no consentimento, keyword aceite em LOW. Integração:
`objective` resolve por keyword sem modelo configurado.

## Adiado

Validação do adapter contra um serviço Azure OpenAI real (precisa de
credenciais); escolha de modelo/deployment por risco da ação; retentativas
e circuit breaker; contabilização de custo/tokens (o teto por sessão é
matéria do It.2 completo).
