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
