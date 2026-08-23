# Design 0005 — Endurecimento da auditoria (It.3, primeiro incremento)

**Estado:** Implementado · **Data:** 2026-08-23
**Depende de:** `design/0001-mcp-pipeline-slice1.md` (cadeia SHA-256 do It.0)

## O buraco que isto fecha

O It.0 encadeou os registos de auditoria com SHA-256 não-keyed. Isso deteta
**edição no sítio**: mexer num campo deixa o `record_hash` desatualizado.

Não deteta as outras duas formas de adulteração, e o próprio código do It.0
dizia-o em comentário:

1. **Truncagem.** Apagar os registos mais recentes deixa uma cadeia mais
   curta e perfeitamente válida. Nada na cadeia sabe quantos registos
   deviam existir.
2. **Reescrita completa.** Quem tenha acesso de escrita ao ficheiro pode
   recalcular a cadeia inteira a partir do zero — o hash é público e não
   depende de nenhum segredo.

Ambas estão ao alcance de exatamente o adversário que a auditoria devia
travar: alguém que já conseguiu escrever no `.db`.

## Duas defesas

**1. Cadeia com chave (HMAC-SHA-256)**

O `record_hash` passa a ser um HMAC com uma chave de 32 bytes guardada
**fora** da base de dados (`AuditKeyFile`, criada na primeira utilização com
permissões só-do-dono). Recalcular a cadeia deixa de ser possível sem o
segredo: acesso de escrita ao `.db` já não chega para forjar.

*Limitação, dita sem rodeios:* um ficheiro no mesmo disco só levanta a
fasquia. Quem leia ficheiros arbitrários como este utilizador obtém a chave.
Separação a sério é keystore do SO (DPAPI/Keychain) ou HSM — fica adiado, mas
a classe recebe bytes de chave, portanto trocar a origem toca só nela.

**2. Âncora de cabeça externa**

Depois de cada append (e só depois do commit, para um rollback não parecer
truncagem), o par `(sequence, record_hash)` é escrito num ficheiro à parte.
A verificação compara: se a âncora está à frente da base de dados, foram
removidos registos. A âncora **nunca anda para trás**, para um escritor
obsoleto não conseguir rebobiná-la e esconder a remoção.

O `AuditVerification` ganhou um `Reason`, porque "cadeia partida na
sequência 7" e "faltam registos a partir da 7" são diagnósticos diferentes e
levam a respostas diferentes.

## Um erro que vale a pena registar

A primeira versão punha a âncora num nome fixo por **diretório**
(`aurora.audit.anchor`). Duas bases de dados na mesma pasta passavam a
partilhar uma âncora e cada uma lia a cabeça da outra como prova de
truncagem — o servidor recusava arrancar. Apanhado pelos testes de
integração, que usam uma base de dados temporária por instância.

A âncora é agora derivada do ficheiro da base de dados (`<db>.anchor`).
A lição não é sobre nomes de ficheiros: um detetor de adulteração com falsos
positivos é tão inútil como não ter nenhum, porque a resposta humana a um
alarme frequente e errado é desligá-lo.

## Configuração

`Aurora:AuditKeyPath` (omissão: `aurora.audit.key` ao lado da base de dados)
e `Aurora:AuditAnchorPath` (omissão: `<db>.anchor`). Ambos configuráveis de
propósito, para um operador poder colocar a chave onde os backups da base de
dados não chegam — guardar os dois juntos num backup anula a defesa.

## Testes

7 testes novos: truncagem detetada apesar da cadeia restante ser válida;
reescrita completa a falhar sem a chave; âncora divergente na mesma
sequência; log vazio sem âncora a verificar bem; âncora a recusar andar para
trás; chave criada uma vez e reutilizada; e chave de tamanho errado a
levantar erro **em vez de** regenerar — regenerar apagaria a prova.

## Adiado

Chave em keystore do SO ou HSM; âncora replicada para fora da máquina
(a defesa atual cai se o atacante apagar base de dados e âncora juntas);
assinatura periódica de checkpoints; e o enriquecimento do pre-image
(decisão/motivo/risco/`via`/`policy_ids`), que é o próximo incremento do It.3.
