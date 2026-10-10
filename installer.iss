; =====================================================================
; Inno Setup Script for Antigravity Hub
; Author: Phùng Toàn (DvenxyWiks)
; Version: 2.5.0
; =====================================================================

#define MyAppName "Antigravity Hub"
#define MyAppVersion "2.5.0"
#define MyAppPublisher "Phùng Toàn (DvenxyWiks)"
#define MyAppURL "https://github.com/wiksgods"
#define MyAppExeName "AntigravityHub.exe"

[Setup]
AppId={{A76E8420-562F-4A9B-BBE7-268A1B5FD9C2}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\AntigravityHub
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=dist\Setup
OutputBaseFilename=AntigravityHub_Setup_v2.5
SetupIconFile=app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
DisableDirPage=no
DisableProgramGroupPage=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "dist\Portable\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "app.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\app.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\app.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent runascurrentuser
