# Design 0009 — Backup e restore (It.3, quinto incremento)

**Estado:** Implementado (backup) · Documentado (restore) · **Data:** 2026-08-23
**Depende de:** `design/0005-it3a-audit-hardening.md`

## Backup

`SqliteBackupService.BackupAsync` usa a **API de backup do próprio SQLite**,
não uma cópia de ficheiro. Copiar um ficheiro WAL com escritores ativos pode
capturar um estado rasgado que só falha muito mais tarde — no restore, que é
o pior momento possível para descobrir isso.

Produz dois ficheiros com carimbo temporal: `aurora-<stamp>.db` e o
`.anchor` correspondente. A âncora viaja com o snapshot porque sem ela uma
base de dados restaurada só se consegue provar *internamente consistente*,
nunca *completa*.

**A verificação corre sobre a cópia, não sobre o original.** Um backup cuja
cadeia não verifica não vale nada, e o momento de saber isso é agora, não
durante um restore em que o original pode já não existir.

## A chave NÃO vai no backup

Decisão deliberada e a mais importante deste documento.

O `design/0005` construiu a defesa da auditoria sobre uma premissa: a chave
de assinatura vive fora da base de dados, portanto quem obtenha acesso de
escrita à base de dados não consegue reescrever a cadeia e reassiná-la.

Meter a chave no mesmo backup deita isso fora. Quem roube o arquivo fica com
tudo o que precisa. Um backup é precisamente o objeto que sai da máquina,
que é copiado para armazenamento de terceiros e que sobrevive anos — o pior
sítio para juntar o segredo aos dados que ele protege.

A chave é responsabilidade do operador: guardada à parte, num sítio onde os
backups da base de dados não chegam. Um teste afirma que nenhum ficheiro de
chave aparece no diretório de backup.

Consequência aceite: restaurar sem a chave dá uma base de dados utilizável
cuja cadeia **não se consegue verificar**. Isso é o comportamento correto —
saber que o arquivo não é verificável é melhor do que confiar nele por
omissão.

## Restore — procedimento, não código

O restore não está automatizado, de propósito. Substituir ficheiros de base
de dados por baixo de um servidor a correr é como se corrompem instalações,
e um comando que o faça convida a fazê-lo à pressa durante um incidente.

1. **Parar o servidor Aurora.** Não avançar com o processo vivo.
2. Colocar `aurora-<stamp>.db` no caminho de `Aurora:DbPath` e
   `aurora-<stamp>.db.anchor` no de `Aurora:AuditAnchorPath`.
3. Apagar quaisquer `-wal` e `-shm` remanescentes do ficheiro antigo. Um WAL
   órfão de outra base de dados é uma forma fiável de corrupção.
4. Repor a chave de assinatura, do sítio à parte onde foi guardada, em
   `Aurora:AuditKeyPath`.
5. Arrancar. O servidor verifica a cadeia no arranque e **recusa arrancar**
   se falhar — se isso acontecer, a resposta é investigar, nunca apagar a
   âncora para o silenciar.

## Testes

5 testes novos: snapshot verificável; âncora incluída; **chave ausente do
backup**; backup adulterado depois do facto a falhar verificação; e backup de
base de dados vazia a verificar bem.

## Adiado

Agendamento automático de backups; rotação e retenção; backup incremental;
cifra do arquivo em repouso; e verificação de que o `.db` restaurado
corresponde à âncora *antes* de arrancar o servidor, em vez de no arranque.
