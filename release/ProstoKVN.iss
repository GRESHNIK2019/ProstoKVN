#define MyAppName "ProstoKVN"
#define MyAppVersion "0.29.1"
#define MyPublisher "ProstoKVN"
#define BuildDir "..\dist\ProstoKVN-v0.29.1-win-x64"

[Setup]
AppId={{F78C462D-7275-4E6B-8C27-77E01D8DBB58}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyPublisher}
DefaultDirName={localappdata}\Programs\ProstoKVN
DefaultGroupName=ProstoKVN
OutputDir=..\dist
OutputBaseFilename=ProstoKVN-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
WizardStyle=modern
DisableProgramGroupPage=yes
CloseApplications=no
RestartApplications=no
UninstallDisplayName=ProstoKVN
VersionInfoVersion={#MyAppVersion}
SetupLogging=yes

[Files]
Source: "{#BuildDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\ProstoKVN"; Filename: "{app}\ProstoKVN.exe"
Name: "{autodesktop}\ProstoKVN"; Filename: "{app}\ProstoKVN.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; Flags: unchecked

[Run]
Filename: "{app}\ProstoKVN.exe"; Description: "Запустить ProstoKVN"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; No forced process termination. User is prompted to close the running VPN.

[Messages]
BeveledLabel=ProstoKVN — VPN клиент для Windows
