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

Para executar no Windows 10/11, use o instalador ou extraia o ZIP portátil. O aplicativo precisa do **Microsoft Edge WebView2 Runtime**. Os perfis de contas e cookies não devem ser enviados ao GitHub.

Para compilar localmente, instale o SDK .NET 8 e execute:

```powershell
dotnet publish PokeIdleMulti/PokeIdleMulti.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

O projeto principal está em `PokeIdleMulti/PokeIdleMulti.csproj`.

## Atualização dentro do aplicativo

O botão **Atualizações** consulta o último Release publicado automaticamente ao iniciar o deck e a cada seis horas, sem reiniciar o jogo. Quando existe um build mais recente, o botão permite baixar o instalador oficial com confirmação do usuário; o aplicativo fecha após abrir o instalador. O ZIP portátil continua disponível nos Releases. A verificação não altera as contas ou as lives em execução.
