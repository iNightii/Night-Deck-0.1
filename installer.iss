#define AppName "Night Deck"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppExe "PokeIdleMulti.exe"
[Setup]
AppId={{D8363C63-B6C1-4F2B-8BFA-31008F61D025}
AppName={#AppName}
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Night Deck
DefaultGroupName={#AppName}
OutputDir=installer-output
OutputBaseFilename=Night-Deck-Setup
Compression=lzma
SolidCompression=yes
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#AppExe}
[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\Night Deck"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\Night Deck"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
[Tasks]
Name: desktopicon; Description: "Criar atalho na área de trabalho"
[Run]
Filename: "{app}\{#AppExe}"; Description: "Abrir Night Deck"; Flags: nowait postinstall skipifsilent
