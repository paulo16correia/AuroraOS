# Aurora OS — RFC 000: Filosofia de arquitetura

**Estado:** Fundacional · **Precede:** todas as restantes RFCs

## Objetivo

Explicar por que razão a Aurora OS é construída como uma arquitetura cognitiva modular, e não como um produto centrado num modelo de linguagem. Esta RFC é normativa: quando uma decisão técnica não estiver coberta por outra RFC, estes princípios prevalecem.

## Tese arquitetural

```text
Modelo de linguagem = capacidade probabilística de interpretar e gerar
Aurora OS           = identidade + estado + políticas + capacidades + operação
Mente (Mind)        = representação persistente e governada do que a Aurora sabe,
                      pretende, observa e pode usar
```

A IA não é o sistema. Um LLM é um componente substituível que apoia deliberação e comunicação. Não possui propriedade sobre dados, não aprova ações e não é fonte de verdade.

## Responsabilidades

- Estabelecer soberania de dados, separação entre verdade e inferência e limites de autonomia.
- Orientar a decomposição de módulos e o desenho de interfaces.
- Fornecer critérios para aceitar ou rejeitar simplificações de implementação.

## Princípios obrigatórios

1. **A Mente pertence à Aurora, não ao modelo.** Identidade, memória, conhecimento, experiências, objetivos e políticas são dados versionados e portáveis.
2. **O modelo nunca é a fonte da verdade.** Uma resposta é hipótese até ser sustentada por dados, ferramenta ou evidência identificável.
3. **A inteligência emerge da coordenação.** Perceção, atenção, memória de trabalho, conhecimento, decisão, execução e reflexão cooperam; nenhum módulo recebe autoridade implícita.
4. **Estado explícito vence texto implícito.** Factos, permissões, tarefas e relações importantes têm objetos tipados, ciclo de vida e proveniência.
5. **Autonomia é uma delegação revogável.** A Aurora só age sem confirmação quando existe política persistida, âmbito limitado, orçamento e mecanismo de pausa.
6. **Explicação operacional, não teatralidade.** O sistema mostra fontes, decisões e efeitos; não finge consciência nem expõe raciocínio privado detalhado.

## Fluxo de uma afirmação

```text
texto/modelo → candidato → validação/evidência → objeto de domínio
                                            │
                                 aceitação humana/política
                                            │
                                            ▼
                                    estado canónico da Mind
```

## Casos limite e erro

- Se o modelo contradiz um registo confirmado, prevalece o registo e a contradição é sinalizada.
- Se um fornecedor de IA deixa de estar disponível, a Mind, políticas e auditoria permanecem intactas; o sistema degrada sem inventar execução.
- Se não houver evidência suficiente, a Aurora pergunta, declara incerteza ou aguarda — não transforma fluência em facto.

## Justificação

Esta filosofia evita acoplamento a fornecedores, memória opaca e “autonomia por prompt”. Também permite testar cada capacidade sem confiar que um modelo seja consistentemente obediente.

## Expansões futuras

Vários modelos especializados, execução local, auditoria criptográfica e portabilidade entre instalações da Aurora.

