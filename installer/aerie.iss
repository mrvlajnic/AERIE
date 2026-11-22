; A per-user installer for the current build. No signing yet: Windows will warn
; on first run until a certificate is bought.
#define AppName "AERIE"

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{8D4E2C1A-7B3F-4E2A-9C6D-3F5A1B7C9E20}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=AERIE contributors
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\out
OutputBaseFilename=AERIE-setup
SetupIconFile=..\src\AERIE\aerie.ico
UninstallDisplayIcon={app}\AERIE-win-x64.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Per-user by default: no administrator prompt to try the app.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Files]
Source: "..\out\win-x64\AERIE-win-x64.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\AERIE-win-x64.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\AERIE-win-x64.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Run]
Filename: "{app}\AERIE-win-x64.exe"; Description: "Open {#AppName}"; Flags: nowait postinstall skipifsilent
