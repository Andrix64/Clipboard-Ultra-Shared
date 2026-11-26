; Installer Windows di CUS (Inno Setup 7).
; Non compilarlo a mano: lo fa scripts/publish.ps1, che prima pubblica l'app self-contained
; e poi passa a ISCC versione e cartella sorgente con /D.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#define AppName "CUS - Clipboard Ultra Shared"
#define AppExeName "cus.exe"

[Setup]
; AppId identifica l'installazione per aggiornamenti e disinstallazione: non cambiarlo mai.
AppId={{BEA38DC1-40C0-4032-819C-09F704DCBFF3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\CUS
DefaultGroupName=CUS
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExeName}
SetupIconFile=..\src\Cus.UI\Assets\cus.ico
OutputDir={#OutputDir}
OutputBaseFilename=cus-setup-{#AppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern dynamic
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Installazione per utente di default (niente UAC, in %LOCALAPPDATA%\Programs\CUS);
; il dialogo permette di scegliere l'installazione per tutti gli utenti.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; Chiude CUS se è in esecuzione (Restart Manager) prima di sostituire i file.
CloseApplications=yes
RestartApplications=no

; Il nome di ogni lingua è il codice del suo file in src/Cus.Core/Localization/Languages (it.json, en.json...):
; finisce in cus.ini e l'app lo usa così com'è. Una lingua nuova dell'app va aggiunta anche qui.
[Languages]
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Lingua scelta nel dialogo dell'installer ({language} = "it", "en"...): CUS la usa finché
; l'utente non ne sceglie un'altra nelle impostazioni (vedi src/Cus.Core/Localization/Loc.cs).
[INI]
Filename: "{app}\cus.ini"; Section: "Setup"; Key: "Language"; String: "{language}"

[UninstallDelete]
Type: files; Name: "{app}\cus.ini"

[Icons]
Name: "{autoprograms}\CUS"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\CUS"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,CUS}"; Flags: nowait postinstall skipifsilent

; I dati utente (%LOCALAPPDATA%\CUS: DB cifrato, identità, amici con patti e fiducia) NON vengono
; rimossi alla disinstallazione: perderli significherebbe dover rifare tutti i patti.
