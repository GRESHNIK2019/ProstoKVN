#define MyAppName "ProstoKVN"
#define MyAppVersion "0.30.2"
#define MyPublisher "ProstoKVN"
#ifndef BuildDir
#define BuildDir "..\dist\ProstoKVN-v0.30.2-win-x64"
#endif

[Setup]
AppId={{F78C462D-7275-4E6B-8C27-77E01D8DBB58}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyPublisher}
AppComments=ProstoKVN VPN client for Windows x64 — .NET included
AppMutex=Local\ProstoKVN.SingleInstance
MinVersion=10.0.17763
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
CloseApplications=yes
RestartApplications=no
UninstallDisplayName=ProstoKVN
VersionInfoVersion={#MyAppVersion}
SetupLogging=yes
UninstallFilesDir={app}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

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
