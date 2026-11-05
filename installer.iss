#define AppName "Night Deck"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{7D9142E2-6D23-4B9D-8B2B-ED0921AC0A18}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Night Deck
DefaultDirName={autopf}\Night Deck
DefaultGroupName=Night Deck
OutputDir=dist
OutputBaseFilename=Night-Deck-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
CloseApplications=yes

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Night Deck"; Filename: "{app}\NightDeck.exe"
Name: "{autodesktop}\Night Deck"; Filename: "{app}\NightDeck.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; GroupDescription: "Atalhos:"; Flags: unchecked

[Run]
Filename: "{app}\NightDeck.exe"; Description: "Abrir Night Deck"; Flags: nowait postinstall skipifsilent
