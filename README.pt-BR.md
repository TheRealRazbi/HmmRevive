# HMM Revive

🇺🇸 **English:** [README.md](README.md)

Jogue **Heavy Metal Machines** de novo. A Hoplon desligou os servidores do jogo em 2022, e o jogo só funciona online.
O HMM Revive traz ele de volta para partidas privadas e gratuitas entre amigos:

- um **servidor de partida** sem janela, montado a partir do próprio código de servidor que veio no jogo (a cena do
  servidor nunca foi distribuída, então o mod reconstrói ela na hora), com bots;
- os **jogadores conectam direto** nesse servidor (pelo modo de conexão direta de desenvolvedor que já existe no
  jogo), por exemplo pelo [Tailscale](https://tailscale.com).

Extras: escolha seu carro e sua skin ao entrar, troque os dois no meio da partida (`/car` e `/skin` no chat),
quantidade, dificuldade e carros dos bots por time, reparo fora de combate.

> Projeto de fã não oficial, sem ligação com a Hoplon e sem aprovação dela. **Nenhum código ou arquivo do jogo da
> Hoplon** está neste repositório ou no kit: o patcher trabalha numa cópia da *sua própria* instalação da Steam, e a
> instalação da Steam nunca é alterada.

## Como jogar

Você precisa de Windows e do Heavy Metal Machines na sua biblioteca da Steam (build `Release.15.00.250`).

1. Baixe o `HMM-Revive-friend-kit-<versão>.zip` em [Releases](../../releases).
2. Descompacte numa pasta cujo caminho **não tenha acentos nem letras especiais**, por exemplo `C:\HMM-Revive`
   (senão o jogo fecha ao abrir). No mesmo disco do jogo, não gasta espaço extra.
3. Rode o `setup.bat` uma vez. Ele encontra o jogo e cria uma cópia modificada em `.\instance`.
4. Uma pessoa hospeda com o `host.bat`; os outros entram com o `play.bat` e o endereço do host.

O passo a passo completo, incluindo como hospedar pelo Tailscale, está no kit e aqui:
[LEIA-ME.txt](tools/friend-kit/LEIA-ME.txt) (Português), [README.txt](tools/friend-kit/README.txt) (English).
A versão (ex.: `HMM Revive 1.1`) aparece na primeira linha de toda janela do kit; diga qual é quando reportar um
problema. O que mudou em cada versão: [CHANGELOG.md](CHANGELOG.md) (em inglês; no kit como `CHANGELOG.txt`).
Na partida, digite `/cars` no chat para ver os carros e `/car <nome>` para trocar, ou `/skins` e `/skin <número>`
para a skin (a troca acontece enquanto você está morto ou entre os rounds).

**Segurança:** o servidor de partida não tem autenticação de verdade. Qualquer pessoa que alcance a porta UDP dele
(9696) pode ocupar uma vaga de jogador livre enquanto ele roda. Compartilhe pelo Tailscale (o LEIA-ME do kit mostra
como liberar só essa porta para quem você convidou), coloque o número exato de jogadores humanos e desligue o servidor
depois de jogar.

## Limitações conhecidas

- Não tem menu principal nem revanche: "Exit game" fecha o jogo, e o host inicia um servidor novo a cada partida.
- A loja dentro da partida não existe mais (a Hoplon tirou do jogo); os carros ficam com os atributos base.
- Se a sua conexão cair por tempo esgotado, o jogo pode te jogar no antigo menu principal, que diz que você foi banido
  por nome impróprio. Ninguém foi banido (não existem mais servidores oficiais): feche o jogo e entre de novo.
- O reparo fora de combate (7% da vida máxima por segundo depois de 5 s sem levar dano) usa valores nossos, não os da
  Hoplon, e ainda pode ser ajustado.

Planejado: uma lista de partidas, para os jogadores acharem partidas rodando sem precisar trocar endereços.

## Compilar a partir do código

Veja a seção "Build from source" do [README em inglês](README.md#build-from-source). Contribuições são bem-vindas;
nunca inclua código descompilado do jogo, arquivos do jogo ou arquivos do jogo modificados.

## Licença

O código deste projeto está sob a [Mozilla Public License 2.0](LICENSE). Ela cobre só o código deste repositório, não
o Heavy Metal Machines, que pertence à Hoplon. Heavy Metal Machines é marca registrada da Hoplon.
Grátis e sem fins comerciais: por favor, não venda acesso nem redistribua os arquivos da Hoplon, modificados ou não.
