# Night Deck — WPF + WebView2

Aplicativo Windows em **C# / .NET 8 WPF**, com quatro sessões independentes do PokeIdle.

## Funcionalidades
- Quatro contas com perfis WebView2 separados em `%APPDATA%\\Night Deck\\Accounts`.
- Layout 2×2 e 1×4 (quatro colunas lado a lado), foco e ocultação de contas.
- Zoom, som e recarregamento por conta; atalhos globais.
- Interface escura com botões arredondados; barra superior preservada ao trocar layouts.
- Atalho **Onde caçar**: https://pokeidle-gg.pages.dev/#hunt
- Menu de atualizações por GitHub Releases, com confirmação antes de baixar e executar o instalador.
- Contador de RAM do processo principal (não inclui todos os processos do WebView2).

## Executar no Windows
```powershell
dotnet restore NightDeck/NightDeck.csproj -r win-x64
dotnet run --project NightDeck/NightDeck.csproj
```

## Publicar
O GitHub Actions compila o aplicativo, cria instalador Inno Setup e tenta publicar um Release. Verifique o resultado em Actions/Releases antes de distribuir.

## Estado da migração
A interface principal foi migrada de WinForms para WPF. As sessões continuam usando os mesmos diretórios de perfil, mas a nova interface ainda precisa de testes reais no Windows. Echo, recompra automática (Restock), personalização de cores e integração embutida da Twitch **não estão implementados**. Os respectivos botões são atalhos ou informam essa limitação. O botão Twitch abre o site no navegador padrão.

A troca de layout preserva as instâncias WebView2, mas a movimentação dos controles entre posições pode causar um breve redesenho. Não há promessa de 60 FPS garantidos.
