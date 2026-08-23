# Design 0003 — `files.write_sandbox` (It.2, segundo incremento)

**Estado:** Implementado · **Data:** 2026-08-23
**Depende de:** `design/0002-it2a-persistent-approval.md` (aprovação persistida)

## Objetivo

O It.2a deu um caminho de aprovação para capabilities ≥MEDIUM, mas a única
capability com efeito real escrevia numa tabela SQLite nossa. Este
incremento entrega a primeira escrita em **filesystem**, que é onde o
hardening de caminho passa a importar: `files.write_sandbox`
(MEDIUM, `approval_required`), confinada a uma raiz de sandbox.

Reaproveita integralmente o gate do It.2a — não há mecanismo de consentimento
novo aqui. O que é novo é a fronteira do sistema de ficheiros.

## Três defesas, por ordem

**1. Validação léxica** (`SandboxPathValidator`, sem I/O, em `Aurora.Core`)

Rejeita em vez de sanitizar: um caminho que não seja obviamente seguro é
recusado, nunca reescrito para algo "suficientemente próximo". Sanitizar é
como nascem os bypasses.

Recusa: travessia (`..`), segmentos `.`, caminhos absolutos ou começados por
separador, UNC (`//`, `\\`), namespaces de device, `:` (cobre ADS e caminhos
relativos a drive), caracteres de controlo, segmentos vazios, segmentos
terminados em espaço ou ponto, nomes de device reservados do Windows
(`CON`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`, com ou sem extensão), e
caminhos acima de 512 caracteres. No fim, confirma que o caminho resolvido
continua sob a raiz, com comparação **ordinal** — construímos o caminho a
partir da raiz, portanto um filho legítimo partilha sempre o prefixo exato;
comparar sem distinguir maiúsculas só alargaria o que conta como "dentro".

**Decisão deliberada:** as regras específicas do Windows são aplicadas em
**todas** as plataformas. Um sandbox escrito em macOS pode ser aberto mais
tarde em Windows, e um nome inerte aqui resolve para um device lá. Ter uma
só regra também significa que os testes cobrem o mesmo comportamento em
qualquer sítio.

**2. Componentes ligados** (`SandboxFileWriter`)

Percorre raiz → alvo e recusa se qualquer componente existente for symlink
ou reparse point, incluindo o próprio ficheiro alvo — sobrescrever um link
seria escrever através dele. A verificação corre **antes** de criar
diretórios (para nunca fazer `mkdir` através de um link) e **outra vez**
depois, já com todos os componentes a existir.

A raiz do sandbox é resolvida através dos seus próprios links uma única vez
na construção: uma raiz que é ela própria um symlink é escolha do operador e
continua a funcionar; só links *dentro* do sandbox são tratados como
tentativa de fuga.

**3. Escrita atómica**

Ficheiro temporário no mesmo diretório (`FileMode.CreateNew`,
`FileOptions.WriteThrough`), flush, e `File.Move(..., overwrite: true)`.
Um leitor nunca observa um ficheiro escrito a meio. O temporário é removido
se algo falhar.

## Risco residual, dito com todas as letras

O .NET não tem `openat`/`O_NOFOLLOW` portável, por isso a verificação de
links e a escrita são syscalls separadas. **Quem consiga criar ficheiros
dentro da raiz do sandbox entre esses dois passos ainda pode ganhar uma
corrida TOCTOU.** Fechar isto a sério exige interop por plataforma e fica
adiado. A mitigação hoje é operacional: a raiz do sandbox deve ser
escrita apenas pelo utilizador do próprio processo Aurora.

Isto não é uma nota de rodapé — é a limitação de segurança conhecida deste
incremento e deve ser revista antes de o sandbox passar a ser partilhado
com outro utilizador ou serviço.

## Superfície de erro

Uma violação de sandbox levanta `SandboxViolationException`, que o Kernel
reporta como `execution_failed` genérico. O motivo **não** volta ao chamador:
caso contrário um cliente mapeava a estrutura do sandbox um caminho recusado
de cada vez. O custo é diagnóstico mais pobre; o enriquecimento do
pre-image de auditoria (It.3) é o sítio certo para registar o motivo do lado
do servidor.

## Configuração

`Aurora:SandboxRoot`, com omissão em
`{LocalApplicationData}/Aurora/sandbox`. Criado no arranque. Os testes de
integração usam uma raiz temporária por instância de factory, para uma
escrita de teste nunca cair no sandbox real.

## Testes

34 testes novos. Validador: cada família de recusa acima, mais aceitação de
caminhos aninhados legítimos e de nomes que só *parecem* devices
(`consortium.txt`). Writer: criação, diretórios aninhados, sobrescrita,
ausência de temporários deixados para trás, recusa de travessia, recusa de
diretório e de ficheiro symlinkados para fora, e raiz symlinkada a
funcionar. Integração: aprovação exigida antes de qualquer coisa tocar o
disco, e — o teste que mais importa — **travessia continua a falhar mesmo
depois de aprovada**, porque a aprovação autoriza a ação, nunca a fuga.

## Adiado

Fecho do TOCTOU por interop; escrita binária (só UTF-8 texto por agora);
leitura/listagem no sandbox; quotas de tamanho ou número de ficheiros.
