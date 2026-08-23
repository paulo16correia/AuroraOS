# Design 0008 — Métricas operacionais (It.3, quarto incremento)

**Estado:** Implementado · **Data:** 2026-08-23
**Depende de:** `design/0005`, `design/0007`

## O que é medido

A lista do design 0001, traduzida para contadores concretos:

| Métrica | Tipo | Porquê importa |
|---|---|---|
| `executionsByOutcome` | contador por resultado | Uma subida de `policy_denied` ou `consent_denied` é sinal de configuração errada ou de um cliente a insistir |
| `pendingApprovals` | **gauge real** | Prompts à espera de um humano; se sobe e não desce, ninguém está a decidir |
| `consentLatency` (média/máximo) | observação | Quanto tempo se espera por uma decisão humana |
| `idempotencyConflicts` | contador | Cliente a reutilizar chaves com inputs diferentes |
| `executionsUnknown` | contador | Efeitos indeterminados; qualquer valor >0 merece atenção |
| `auditFailures` | contador | O registo de segurança está a degradar-se |

## Duas decisões

**O gauge de aprovações pendentes é lido da base de dados**, não contado em
memória. Um par de contadores criado/resolvido pareceria mais barato, mas
desvia-se do livro-razão a cada reinício ou expiração — e um gauge que mente
sobre "quantos humanos estão a ser esperados" é pior do que não existir.
Os restantes contadores são de tempo-de-vida do processo, e isso está dito
no `MetricsSnapshot`: um contador reposto por um crash é indistinguível de um
período calmo, e quem lê tem de saber disso.

**O endpoint é HTTP, não uma tool MCP.** Fica em `GET /metrics`, atrás do
mesmo guarda de loopback e bearer que a superfície MCP. Expor isto como
quarta tool daria a um reasoner não-confiável uma visão de com que frequência
os seus pedidos estão a ser recusados — informação útil para quem esteja a
sondar os limites da policy. As métricas são para o operador.

A latência de consentimento é calculada a partir dos carimbos do próprio
registo de aprovação (`created_at` → `decided_at`), não de um cronómetro em
memória, para uma decisão que atravesse um reinício continuar a contar. Valores
negativos (desvio de relógio) são fixados a zero em vez de enviesarem a média.

## Testes

9 testes novos. Unitários: snapshot vazio, contagem por resultado, média e
máximo de latência, máximo a sobreviver a um valor menor posterior, latência
negativa fixada a zero, independência dos contadores, e 200 atualizações
concorrentes sem perdas. Integração: `/metrics` exige bearer, e reporta
execuções e aprovações pendentes reais.

## Adiado

Exportação em formato Prometheus; histograma de latência a sério (só média e
máximo por agora); métricas por capability; e persistência dos contadores
entre reinícios.
