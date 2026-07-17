# Aurora OS — RFC 032: Motor de curiosidade governada

**Estado:** Normativo · **Depende de:** RFC 028, 030–031, 033–034, LAW-006

## Objetivo

Identificar lacunas de conhecimento potencialmente úteis e propor investigação de baixo risco. Curiosidade é uma capacidade de descoberta governada, não um pretexto para recolher dados, navegar sem limite ou contactar terceiros.

## Arquitetura

```text
Padrões recorrentes + lacunas do World Model + objetivos autorizados
                              │
                              ▼
                   CuriosityProposal
                              │
                 valor × risco × recursos × consentimento
                              │
                              ▼
           descartar | agendar pesquisa permitida | pedir aprovação
```

## Estruturas de dados

```text
CuriosityProposal
  id, question, rationale_refs[], expected_value, scope
  allowed_sources[], sensitivity_limit, resource_budget
  status: CANDIDATE|APPROVED|SCHEDULED|RESEARCHING|LEARNED|REJECTED|EXPIRED
  approval_required, result_refs[], review_at
```

## Interfaces

```text
Curiosity.detect(mind_snapshot) -> CuriosityProposal[]
Curiosity.evaluate(proposal, situation, resources) -> DecisionOption
Curiosity.schedule(proposal_id, approval) -> Task
```

## Regras obrigatórias

1. Curiosidade só pode usar fontes e classificação de dados explicitamente permitidas.
2. Não pode aceder a contas privadas, enviar mensagens, comprar, alterar estado externo ou inferir dados pessoais sensíveis sem objetivo e aprovação próprios.
3. Pesquisa autónoma é limitada por orçamento, janela temporal, necessidades mais urgentes e preferência do utilizador.
4. Resultados percorrem Perceção e LAW-001; pesquisar não cria conhecimento automaticamente.

## Casos limite e erro

- **Utilizador menciona Rust repetidamente:** pode surgir proposta de pesquisa, nunca subscrição/compra/ação externa automática.
- **Sistema ocupado:** proposta fica `DEFERRED`/`EXPIRED`, sem competir com segurança ou tarefas do utilizador.
- **Fonte hostil:** conteúdo é tratado como observação não confiável e não altera políticas.

## Justificação

Curiosidade bem limitada permite evolução útil sem transformar a Aurora numa máquina de recolha indiscriminada.

## Expansões futuras

Catálogos de fontes confiáveis, aprendizagem por projeto, avaliações de utilidade e listas de temas proibidos.

