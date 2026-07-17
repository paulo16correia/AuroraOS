# Aurora OS — RFC 00: Visão do Projeto

**Estado:** Proposta inicial  
**Código do projeto:** Aurora OS  
**Categoria:** Arquitetura / Produto  
**Idioma normativo:** Português europeu  
**Público-alvo:** engenharia, produto, segurança e operação

## 1. Resumo

Aurora OS é a designação provisória de uma plataforma para uma entidade digital persistente: um sistema de software que mantém uma identidade configurável, memória com proveniência, objetivos, contexto operacional e capacidade controlada de agir através de ferramentas digitais.

O projeto não pretende simular consciência, emoções reais ou autonomia sem limites. Pretende criar um assistente pessoal duradouro e auditável que consiga trabalhar em múltiplos canais — inicialmente conversa, e posteriormente email, Discord, calendário, documentos, navegador e serviços de desenvolvimento — mantendo continuidade entre interações e operando dentro de permissões explícitas.

O modelo de linguagem é um componente substituível do sistema, não o sistema inteiro. A identidade, a memória, as políticas, os registos de auditoria e as integrações devem sobreviver à troca de fornecedor ou modelo de IA.

## 2. Visão do projeto

A visão da Aurora OS é tornar possível uma pessoa virtual útil, consistente e responsável, alojada de forma persistente e capaz de acompanhar os projetos, preferências, decisões e rotinas do seu utilizador ao longo do tempo.

Em vez de tratar cada mensagem como uma sessão isolada, a Aurora OS deve interpretar as interações como acontecimentos num ambiente contínuo. Deve conseguir recordar decisões anteriores, relacionar pessoas, projetos, documentos e tarefas, propor próximos passos e executar ações autorizadas. A continuidade deve resultar de dados estruturados, regras claras e recuperação de contexto verificável; nunca de uma alegação infundada de que o sistema “se lembra de tudo”.

O resultado desejado é um sistema operativo pessoal cognitivo: uma plataforma sobre a qual podem ser adicionadas capacidades sem sacrificar segurança, explicabilidade, controlo do utilizador ou manutenção a longo prazo.

## 3. Objetivos

### 3.1 Objetivos de produto

1. Fornecer uma interface conversacional natural em português europeu, com uma identidade e estilo de comunicação consistentes.
2. Manter memória persistente, pesquisável e corrigível sobre factos, acontecimentos, documentos, procedimentos, preferências e decisões.
3. Representar relações importantes através de um grafo de conhecimento, permitindo navegar entre entidades relacionadas e explicar a origem das ligações.
4. Converter pedidos de maior dimensão em objetivos, planos, tarefas, dependências e estados verificáveis.
5. Utilizar ferramentas digitais por meio de contratos uniformes, permissões granulares e registos de execução.
6. Integrar progressivamente canais externos, incluindo email, Discord, calendário, ficheiros, navegador e sistemas de desenvolvimento.
7. Permitir operação contínua numa VPS, com observabilidade, cópias de segurança, recuperação e atualização segura.
8. Permitir trocar modelos de IA e fornecedores sem perder os dados ou a identidade operacional da Aurora.

### 3.2 Objetivos de engenharia

1. Separar claramente raciocínio, memória, planeamento, execução, política e interfaces externas.
2. Conceber módulos com contratos estáveis e versionados, para que possam evoluir de forma independente.
3. Garantir que todas as ações externas relevantes são rastreáveis: quem iniciou, que política autorizou, que ferramenta foi usada, que dados foram consultados e qual foi o resultado.
4. Tratar falhas de serviços externos como situações normais: limitar tentativas, informar o utilizador, preservar estado e evitar ações duplicadas.
5. Começar com uma arquitetura simples de operar; a divisão em serviços independentes só deve ocorrer quando oferecer uma vantagem concreta de fiabilidade, escala ou isolamento.

## 4. Não objetivos

Os seguintes pontos estão explicitamente fora do âmbito inicial:

- Alegar, medir ou tentar criar consciência, sentimentos reais, vontade própria ou estatuto moral humano.
- Executar ações irreversíveis, financeiras, legais ou com impacto externo significativo sem autorização humana adequada.
- Guardar credenciais ou segredos em prompts, mensagens, registos não cifrados ou memória pesquisável.
- Substituir aconselhamento profissional médico, jurídico, financeiro ou de segurança.
- Construir uma solução de vigilância, recolha indiscriminada de dados ou monitorização oculta de terceiros.
- Dar à entidade acesso administrativo ilimitado à VPS, contas pessoais ou serviços de terceiros.
- Prometer que toda a informação será automaticamente recordada com precisão perfeita.
- Resolver simultaneamente todas as integrações e interfaces antes de validar o núcleo de memória, política e execução controlada.

## 5. Definição: entidade digital persistente

Para efeitos desta especificação, uma **entidade digital persistente** é um sistema de software com os seguintes atributos:

1. **Identidade configurada:** possui nome, idioma, estilo de comunicação, valores operacionais, limites e objetivos definidos num perfil versionado.
2. **Continuidade de estado:** conserva, entre sessões, dados aprovados sobre factos, eventos, tarefas, relações e preferências.
3. **Perceção por canais:** recebe sinais de interfaces autorizadas, como chat, email ou Discord, e normaliza-os num formato comum de evento.
4. **Deliberação limitada:** avalia um pedido com contexto, políticas e capacidades disponíveis para decidir se responde, pergunta, planeia, executa ou recusa.
5. **Ação controlada:** usa ferramentas apenas dentro dos respetivos limites de permissão e com confirmação quando exigida.
6. **Aprendizagem governada:** pode propor e consolidar memórias ou procedimentos, mas cada registo deve ter origem, confiança, âmbito e mecanismos de correção.
7. **Auditabilidade:** decisões e ações relevantes podem ser inspecionadas sem expor raciocínio privado detalhado nem segredos.

Persistência não significa atividade autónoma permanente. Significa que a identidade e o estado sobreviverem a reinícios, atualizações e intervalos sem interação, de acordo com as políticas de retenção e privacidade.

## 6. Princípios orientadores

### 6.1 O utilizador mantém o controlo

A Aurora deve ser útil sem assumir autoridade. O utilizador pode ver, corrigir, apagar, exportar e limitar memórias, integrações e permissões. A ausência de uma resposta não constitui consentimento.

### 6.2 Segurança antes de conveniência

Uma ação potencialmente prejudicial deve ser bloqueada, limitada ou submetida a confirmação, mesmo que isso torne a experiência menos imediata. A arquitetura deve assumir que o modelo pode interpretar mal pedidos, que serviços externos podem falhar e que conteúdo recebido pode tentar manipular o sistema.

### 6.3 Memória com proveniência e direito a correção

Cada memória duradoura deve indicar de onde veio, quando foi criada, qual a confiança, quem pode aceder-lhe e como pode ser revista ou eliminada. Memória não é verdade absoluta; é informação sujeita a confirmação e atualização.

### 6.4 Separação de responsabilidades

O componente que gera linguagem não deve deter, sozinho, a decisão final sobre permissões, execução ou persistência. Política, ferramentas, armazenamento e interfaces devem aplicar controlos próprios.

### 6.5 Menor privilégio

Cada integração recebe apenas o acesso indispensável. Capacidades de leitura, criação, envio, alteração, eliminação e administração são distintas. As credenciais são mediadas por um gestor de segredos e nunca entregues como texto ao modelo.

### 6.6 Explicabilidade proporcional

A Aurora deve explicar ao utilizador o que fez, os dados relevantes que consultou e o motivo de pedir confirmação. A explicação será uma síntese operacional verificável, não uma exposição de cadeias internas de raciocínio.

### 6.7 Falhar de forma segura

Quando existir incerteza relevante, permissões insuficientes, conflito de políticas ou erro de ferramenta, o comportamento padrão é parar a ação, preservar evidência e pedir orientação quando necessário.

### 6.8 Evolução sem perda de identidade

Modelos, fornecedores e implementações internas podem mudar. Os dados de domínio, políticas, contratos de ferramentas e perfil da entidade devem permanecer portáveis e versionados.

### 6.9 Privacidade por defeito

Deve ser guardado o mínimo necessário, pelo menor período necessário e com acesso restrito. Dados particularmente sensíveis exigem classificação, cifragem e regras específicas de retenção.

## 7. Filosofia de arquitetura de alto nível

A Aurora OS será desenhada como uma composição de subsistemas, não como uma única chamada a um modelo de linguagem.

```text
Utilizador e canais externos
            │
            ▼
  Gateway de interfaces e eventos
            │
            ▼
      Núcleo cognitivo
  ┌─────────┼──────────┐
  ▼         ▼          ▼
Contexto  Política   Planeamento
  │         │          │
  └────┬────┴────┬─────┘
       ▼         ▼
  Memória e   Gestor de
  conhecimento ferramentas
       │         │
       ▼         ▼
 Base de dados  Integrações autorizadas
 e grafo        (email, Discord, browser, etc.)
```

### 7.1 Gateway de interfaces e eventos

Cada canal externo transforma mensagens, comandos, ficheiros e notificações num formato de evento comum. O gateway autentica a origem, aplica limites de taxa, conserva metadados essenciais e impede que regras presentes em conteúdo externo sejam tratadas como instruções de sistema.

### 7.2 Núcleo cognitivo

O núcleo coordena o ciclo de trabalho: interpretar o evento, recolher contexto, aplicar políticas, escolher uma resposta ou ação, criar um plano quando necessário e produzir uma síntese para o utilizador. O núcleo não acede diretamente a credenciais nem executa comandos arbitrários.

O modelo de IA pode apoiar interpretação, síntese, planeamento e geração de linguagem. Deve estar atrás de uma abstração de fornecedor, com registo de versão, limites de custo, regras de dados e estratégias de indisponibilidade.

### 7.3 Memória e conhecimento

A memória combina dados estruturados e pesquisa semântica. O grafo de conhecimento representa entidades e relações relevantes. A recuperação deve ser seletiva, explicável e limitada ao contexto necessário. Nenhuma fonte isolada deve dominar decisões críticas sem validação.

### 7.4 Planeamento e objetivos

Pedidos complexos devem ser representados como objetivos explícitos, compostos por tarefas com dependências, estado, prioridade, responsável, critérios de conclusão e necessidade de aprovação. Planear não autoriza executar.

### 7.5 Gestor de ferramentas

Ferramentas seguem uma interface comum e declaram capacidades, entradas, saídas, efeitos, limites, requisitos de aprovação e classificação dos dados tratados. O gestor valida cada invocação contra a política antes de a executar e regista o resultado.

### 7.6 Política, segurança e auditoria

O motor de política decide se uma ação é permitida, exige confirmação ou é proibida. A decisão deve ser determinística sempre que possível, independente da persuasão do conteúdo recebido e registrada para auditoria. Os segredos ficam num cofre ou gestor dedicado, com referências opacas e rotação suportada.

## 8. Critérios de sucesso

O projeto será considerado bem sucedido por etapas, não por uma demonstração visual isolada.

### 8.1 Núcleo mínimo viável

- O utilizador consegue conversar em português europeu com um perfil de identidade consistente.
- A Aurora guarda e recupera memórias explicitamente aprovadas, mostrando a respetiva origem.
- O utilizador consegue corrigir ou apagar uma memória e confirmar que deixa de ser usada.
- Uma ação externa de teste passa por política, aprovação e registo de auditoria.
- O sistema suporta reinício sem perda dos dados persistentes essenciais.

### 8.2 Operação segura

- Credenciais não aparecem em respostas, prompts, registos aplicacionais nem exportações de memória.
- Cada ferramenta possui permissões separadas por tipo de ação.
- Ações sensíveis exigem confirmação clara e não podem ser desencadeadas por conteúdo não confiável.
- Existe cópia de segurança testada e procedimento documentado de restauro.

### 8.3 Utilidade sustentada

- A Aurora acompanha pelo menos um projeto real ao longo de várias semanas sem perder decisões importantes ou criar duplicados frequentes.
- Consegue produzir e manter um plano de tarefas com estado verificável.
- Integra pelo menos um canal externo de baixo risco e um fluxo de aprovação para comunicação externa.
- O utilizador consegue inspecionar o histórico de ações e compreender o que ocorreu.

## 9. Restrições e pressupostos

### 9.1 Restrições técnicas

- A primeira implementação deve poder funcionar numa VPS modesta, usando serviços externos de IA para inferência quando necessário.
- PostgreSQL será a fonte transacional principal na fase inicial; pesquisa vetorial e grafo podem começar no mesmo ecossistema ou em serviços separados apenas quando justificável.
- A arquitetura deve ser contentorizável e suportar variáveis de configuração por ambiente.
- O sistema deve tolerar falhas temporárias de APIs, limites de utilização, reinícios e tarefas interrompidas.

### 9.2 Restrições de segurança e privacidade

- Dados de terceiros só podem ser tratados para uma finalidade autorizada e dentro das regras aplicáveis.
- A retenção de mensagens, documentos e registos deve ser configurável.
- Informação confidencial deve ser classificada antes de se tornar pesquisável ou ser enviada a um fornecedor de IA.
- O sistema deve defender-se contra injeção de instruções em emails, páginas web, anexos, ficheiros e mensagens de chat.

### 9.3 Restrições de produto

- Não se deve prometer autonomia total no lançamento inicial.
- A confiança deve aumentar gradualmente: observação, sugestão, rascunho, execução com aprovação e só depois automação limitada e revogável.
- O projeto deve privilegiar qualidade de memória e segurança sobre quantidade de integrações.

## 10. Glossário

| Termo | Definição |
| --- | --- |
| **Aurora** | A identidade digital configurada que interage com o utilizador. |
| **Aurora OS** | A plataforma técnica que implementa a Aurora e os seus subsistemas. |
| **Evento** | Representação normalizada de algo recebido ou observado por um canal. |
| **Memória episódica** | Registo de um acontecimento datado, com contexto e origem. |
| **Memória semântica** | Facto ou conhecimento relativamente estável, sujeito a revisão. |
| **Memória procedimental** | Procedimento ou método reutilizável para realizar uma tarefa. |
| **Grafo de conhecimento** | Conjunto de entidades e relações tipadas, navegável e com proveniência. |
| **Objetivo** | Resultado desejado, persistente, que pode originar um plano. |
| **Tarefa** | Unidade de trabalho concreta, com estado e critérios de conclusão. |
| **Ferramenta** | Integração com uma capacidade externa ou interna, protegida por contrato e permissões. |
| **Política** | Regra verificável que permite, condiciona ou proíbe uma operação. |
| **Aprovação** | Consentimento explícito do utilizador para uma ação delimitada. |
| **Cofre de segredos** | Serviço ou mecanismo que armazena e disponibiliza credenciais sem as expor ao modelo. |
| **Proveniência** | Informação sobre a origem, data, autor, confiança e transformações de um dado. |
| **Canal não confiável** | Fonte cujo conteúdo não pode alterar regras, permissões ou instruções do sistema. |

## 11. Roadmap futuro

O roadmap descreve uma sequência de aprendizagem e entrega. Não é um compromisso de datas; cada fase só avança quando os critérios de segurança e qualidade da fase anterior forem demonstrados.

### Fase 0 — Especificação e decisões fundacionais

Criar o repositório de especificações, definir princípios, classificação de dados, modelo de permissões, contratos de ferramentas e critérios de aceitação. Esta RFC é o primeiro documento dessa fase.

### Fase 1 — Núcleo conversacional e identidade

Implementar uma interface local de conversa, perfil de personalidade versionado, adaptação a modelos de IA, armazenamento transacional, registo de eventos e auditoria básica. Não incluir autonomia nem integrações com poder de escrita.

### Fase 2 — Memória governada e grafo inicial

Adicionar memórias episódicas, semânticas e procedimentais; recuperação de contexto; correção e eliminação pelo utilizador; entidades e relações com proveniência; e métricas de precisão e duplicação.

### Fase 3 — Objetivos, planeamento e aprovações

Introduzir objetivos persistentes, tarefas, dependências, reflexão pós-execução e fluxos de aprovação. A Aurora poderá criar planos e rascunhos, mas ações externas continuarão dependentes de confirmação.

### Fase 4 — Primeiras ferramentas controladas

Adicionar uma integração de baixo risco e um conjunto limitado de ações, por exemplo leitura de calendário ou preparação de rascunhos de email. Validar políticas, cofre de segredos, registos e recuperação de falhas antes de ampliar capacidades.

### Fase 5 — Operação em VPS e painel de controlo

Preparar contentores, cópias de segurança, monitorização, alertas, atualizações e um painel para memórias, objetivos, ferramentas, permissões e auditoria.

### Fase 6 — Ecossistema e autonomia limitada

Adicionar Discord, email, navegador, documentos e serviços de desenvolvimento gradualmente. Automatizações recorrentes só serão ativadas por regras explícitas, com âmbito restrito, limites de custo, mecanismo de pausa e revisão periódica.

### Fase 7 — Maturidade e portabilidade

Reforçar testes de segurança, exportação e migração de dados, substituição de modelos, observabilidade avançada, avaliação de qualidade de memória e documentação para contribuidores.

## 12. Decisões abertas

Esta RFC define direção, não todos os pormenores de implementação. Os documentos seguintes devem resolver, pelo menos, as seguintes decisões:

1. Modelo exato de classificação, retenção, cifragem e eliminação de dados.
2. Esquema de dados para memórias, entidades, relações, objetivos e auditoria.
3. Matriz de permissões e fluxos de aprovação por categoria de ferramenta.
4. Política de seleção de modelos, tratamento de dados e limites de custo.
5. Estratégia de recuperação semântica, avaliação de relevância e prevenção de memórias falsas ou duplicadas.
6. Modelo de ameaças, incluindo injeção de instruções, acesso indevido, exfiltração de dados e ações duplicadas.
7. Critérios objetivos para permitir automação recorrente em cada integração.

## 13. Conclusão

Aurora OS deve ser construída como uma plataforma confiável antes de ser uma plataforma impressionante. A característica que a distinguirá não será responder depressa ou ter muitas integrações: será manter continuidade útil, agir apenas quando autorizada e permitir que o utilizador compreenda, corrija e controle o seu funcionamento.

Esta visão estabelece o contrato fundacional do projeto. As RFCs seguintes transformarão estes princípios em especificações para o núcleo cognitivo, memória, grafo de conhecimento, planeamento, ferramentas, personalidade, segurança, interfaces e operação.

## 14. Contratos de visão, casos limite e evolução

### Estruturas e interfaces de alto nível

```text
SystemCapability
  id, name, maturity: PLANNED|EXPERIMENTAL|SUPPORTED|RETIRED
  owner_component, risk_class, dependencies[], enabled_by_default

SystemHealth
  overall: HEALTHY|DEGRADED|UNAVAILABLE
  components[], last_backup_at, policy_version, observed_at

CapabilityRegistry.list(access_context) -> SystemCapability[]
HealthService.read() -> SystemHealth
```

### Regras obrigatórias

O produto DEVE declarar claramente capacidades experimentais; não DEVE apresentar um rascunho como ação concluída; e DEVE operar de forma útil quando componentes não críticos estão degradados.

### Casos limite e erro

Se uma funcionalidade depender de um componente ainda não especificado ou aprovado, fica `PLANNED`. Se a plataforma operar sem uma integração, deve conservar tarefas e explicar a limitação, em vez de simular execução. Falha da persistência torna o sistema indisponível para ações, para não perder auditoria.

### Justificação e expansões

O registo explícito de maturidade protege o utilizador contra promessas implícitas e permite lançar capacidades de forma gradual. Futuramente, o registo pode suportar licenciamento, equipas e múltiplos ambientes.
