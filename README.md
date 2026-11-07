# Night Deck — PokeIdle Multiscreen

Aplicativo Windows Forms (.NET 8 + Microsoft Edge WebView2) que reúne até quatro contas de [PokeIdle](https://pokeidle.io/app) numa única janela.

## Origem deste projeto

A referência principal é o pacote **PokeIdleMulti-2026.11.05.2**, fornecido pelo usuário. Os arquivos C#, JavaScript e os componentes das lives Twitch/Kick do pacote já correspondem aos arquivos do projeto (desconsiderando a quebra de linha final). **Não foram importados a pasta `nudes/` nem o arquivo `deck.ico` do pacote.** O ícone próprio que já estava no repositório foi preservado.

O projeto mantém a licença MIT e a atribuição do autor original em [LICENSE](LICENSE).

## Recursos disponíveis no código

- Até quatro contas, cada uma com seu perfil WebView2, controles próprios de zoom e layouts;
- Eco de cliques entre contas (`Echo.cs`);
- Janelas de lives **Twitch e Kick**, com menus de canais, vídeo, chat e opções de reprodução;
- Recompra / restock e ferramenta de resgates;
- Controles de economia de recursos, voto no TopIdle e extensões;
- Configurações e controles de interface do projeto original.

Esses recursos estão implementados no código, mas fluxos específicos ainda precisam ser validados em execução no Windows. Este é um projeto de fã não oficial.

## Baixar

- [Releases — versões publicadas](https://github.com/iNightii/Night-Deck-0.1/releases/latest)
- [Actions — compilações e artefatos](https://github.com/iNightii/Night-Deck-0.1/actions)

A cada push na branch `main`, o workflow do Windows compila o aplicativo. Quando as etapas terminam com sucesso, ele publica um **Release** com o ZIP portátil e o instalador. O download de um novo Release **não instala a atualização automaticamente dentro do aplicativo**.

## Executar e compilar

Para executar no Windows 10/11, prefira o instalador (que instala o app em uma pasta própria e cria somente um atalho opcional na Área de Trabalho). Se escolher o ZIP portátil, extraia em **Downloads** ou em outra pasta: os arquivos já vêm agrupados dentro da pasta **Night Deck**, evitando deixá-los soltos na Área de Trabalho. O aplicativo precisa do **Microsoft Edge WebView2 Runtime**. Os perfis de contas e cookies não devem ser enviados ao GitHub.

Para compilar localmente, instale o SDK .NET 8 e execute:

```powershell
dotnet publish PokeIdleMulti/PokeIdleMulti.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

O projeto principal está em `PokeIdleMulti/PokeIdleMulti.csproj`.

## Atualização dentro do aplicativo

O botão **Atualizações** consulta o último Release publicado automaticamente ao iniciar o deck e a cada seis horas, sem reiniciar o jogo. Quando existe um build mais recente, o botão permite baixar o instalador oficial com confirmação do usuário; o aplicativo fecha após abrir o instalador. O ZIP portátil continua disponível nos Releases. A verificação não altera as contas ou as lives em execução.

## Limite visual de quadros

Em **ULTRA → 🎞 Quadros do mapa**, selecione **15 FPS (padrão)**, **30 FPS**, **45 FPS**, **60 FPS** ou **Sem limite**. A opção vale para todas as contas e é salva em `deck.json`. A mudança funciona sem recarregar o PokeIdle. Sem limite significa que o deck não restringe o número de quadros (a taxa real depende do monitor e do navegador). O controle atua sobre callbacks de animação (`requestAnimationFrame`) da página do jogo, portanto também pode afetar outras animações da mesma página. Não controla os servidores do jogo, nem as janelas separadas da Twitch e Kick.

A versão ZIP 2026.11.05.2 recebida não continha este controle, apesar de as notas do Release original o mencionarem; esta é uma implementação nova.
