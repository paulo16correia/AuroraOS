# LAW-005 — Todo o estado tem dono, ciclo de vida e fronteira

## Enunciado

Cada objeto persistente ou temporário deve declarar quem o possui, onde vive, como nasce, como evolui, como termina e quem o pode ler/escrever.

## Controlo verificável

- O modelo de domínio exige `tenant_id`, origem, estado, versão, retenção/TTL e política de acesso.
- Não são permitidas tabelas, caches ou filas sem proprietário e contrato de retenção.
- Objetos temporários expiram; objetos persistentes têm estratégia de revisão, arquivo ou eliminação.

## Justificação

Esta Lei impede “estado órfão”, o principal mecanismo pelo qual sistemas de agentes se tornam impossíveis de depurar ou apagar corretamente.

