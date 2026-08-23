# Design 0006 — Enriquecimento do pre-image de auditoria (It.3, segundo incremento)

**Estado:** Implementado · **Data:** 2026-08-23
**Depende de:** `design/0005-it3a-audit-hardening.md`, `design/0004-it1-reasoner.md`

## Porquê agora

O registo de auditoria do It.0 guardava *o que aconteceu*: principal, ação,
hash do input, resultado. Isso chegava enquanto toda a ação era nomeada
explicitamente por quem chamava.

O It.1 mudou isso. Desde que um reasoner não-confiável pode escolher a ação,
"o que aconteceu" deixa de permitir reconstruir a decisão: o registo não
distinguia uma ação que um humano nomeou de uma que um modelo propôs.

## O que passa a ficar registado

`AuditEntry` acrescenta cinco campos, todos opcionais (um pedido rejeitado
antes da resolução não tem risco nem `via` para reportar):

| Campo | Para quê |
|---|---|
| `risk` | Nível declarado da capability no momento da execução |
| `via` | `explicit`, `reasoner` ou `keyword` — quem escolheu a ação |
| `decision` | Resultado do consentimento (`auto_low`, `granted`, …) |
| `policy_ids` | Que regras de policy decidiram |
| `reason` | Motivo textual de uma recusa |

O `via` é o mais importante dos cinco. É o que permite responder, meses
depois, à pergunta que interessa quando algo corre mal: **isto foi um humano
ou foi o modelo?**

## Os campos são assinados, não só guardados

Entram todos no pre-image do HMAC. Um teste existe só para isto: reescrever
apenas `decision` e `via` na base de dados tem de partir a verificação. Se
fossem guardados fora da assinatura, o "porquê" seria silenciosamente
forjável — o que seria pior do que não o registar, porque daria confiança
indevida.

O pre-image passou a incluir uma etiqueta de versão (`v2`) como primeiro
campo. Uma futura adição de campos passa a ser uma mudança de formato
reconhecível, em vez de uma falha de verificação inexplicável.

## Consequência operacional: instalações existentes

**Mudar o pre-image invalida cadeias de auditoria criadas por versões
anteriores.** O servidor verifica a cadeia no arranque e **recusa arrancar**
se a verificação falhar (fail-closed, por desenho).

Uma instalação existente que atualize para esta versão não arranca. Isto é
aceitável agora porque o projeto é pré-produção e não há instalações a
proteger; deixa de o ser assim que houver. Antes da primeira instalação
real é preciso uma de duas coisas:

- verificação por versão de pre-image, mantendo registos `v1` verificáveis
  com a regra `v1` (o campo de versão já existe para isto); ou
- um passo de migração explícito que arquive a cadeia antiga, a sele, e
  comece uma nova — nunca uma que a apague.

O mesmo se aplica ao It.3a, que já tinha trocado SHA-256 por HMAC.

## Testes

4 testes novos: campos enriquecidos cobertos pela assinatura (adulterar só
`decision`/`via` parte a cadeia); execução normal a registar risco, `via` e
decisão; recusa de policy a registar motivo; e resolução por reasoner
distinguível de explícita no registo.

## Adiado

Verificação por versão de pre-image (ver acima); exportação do log em
formato assinado para fora da máquina; retenção e rotação.
