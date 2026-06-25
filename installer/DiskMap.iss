; DiskMap installer script (Inno Setup 6).
; Build the app first (see installer/build.ps1), then compile this with ISCC.exe.
;
; Installs per-user, no admin/UAC required — consistent with DiskMap's own
; "never requires admin to run" design.

#define MyAppName "DiskMap"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "aldo-mcs"
#define MyAppURL "https://github.com/aldo-mcs/DiskMap"
#define MyAppExeName "DiskMap.App.exe"
#define PublishDir "..\src\DiskMap.App\bin\Release\net10.0-windows\win-x64\publish"

[Setup]
AppId={{239B114B-21EF-4A63-9C1F-BCBF6F1C3748}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=Output
OutputBaseFilename=DiskMap-Setup-{#MyAppVersion}
SetupIconFile=..\src\DiskMap.App\diskmap.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Leaves scan history/settings in %LOCALAPPDATA%\DiskMap untouched on uninstall by design.
Type: filesandordirs; Name: "{app}"
