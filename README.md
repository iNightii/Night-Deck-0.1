# Night Deck — reconstrução em C#

Projeto novo em .NET 8 WinForms + WebView2, quatro sessões independentes, layout 2×2 / 1×4 horizontal, zoom individual e geral, som, recarregar, foco, ocultar, fullscreen e indicador de memória do processo principal.

## Executar
No Windows, com .NET 8 SDK e WebView2 Runtime:

```powershell
dotnet restore NightDeck/NightDeck.csproj
dotnet run --project NightDeck/NightDeck.csproj
```

## Compilar
```powershell
dotnet publish NightDeck/NightDeck.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

## Estado
Esta é a primeira base, ainda não um aplicativo final. O botão Cores e o Echo são placeholders explícitos. Sem Twitch, Kick, restock, extensões, limpeza forçada de RAM ou autoatualização. O contador de RAM mostra o processo principal; os processos WebView2 não são incluídos para não atribuir memória de outros aplicativos ao Night Deck.

Não substituir a branch main do projeto antigo sem autorização explícita.

## Atualizações (novo)

O menu **Atualizações → Verificar agora** consulta a versão mais recente publicada nos Releases do GitHub. Uma verificação também é executada automaticamente alguns segundos após a abertura. Se não houver atualização, a verificação automática não exibe janela. Quando há uma versão nova, o programa oferece baixar o instalador e pergunta antes de executá-lo e fechar as contas.

**Configuração necessária:** preencha `NightDeck/update-source.json` com `"repository": "usuario/repositorio"`, usando o endereço do novo repositório. Publique Releases com tags numéricas (por exemplo, `v1.0.1`) e anexe um instalador Windows `.exe` cujo nome contenha `Night`. O número da tag deve ser maior que `<Version>` no arquivo `NightDeck.csproj`.

A versão atual do workflow publica somente um executável em Artifacts, **não cria Releases nem instalador**. A publicação automática do instalador será configurada depois que o endereço do novo repositório for informado. Não execute arquivos de Releases de terceiros.
