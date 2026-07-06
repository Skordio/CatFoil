; CatFoil per-user installer (Inno Setup 6).
;
; Build via scripts\build-installer.ps1, which publishes the self-contained
; single-file EXE to dist\publish\ and passes /DMyAppVersion=<ver> to ISCC.
;
; Per-user, no-admin install (PrivilegesRequired=lowest): lands in
; %LOCALAPPDATA%\Programs\CatFoil. The app self-elevates at runtime only when it
; needs to block elevated windows, so the installer itself never needs admin.
; All user state (settings.json, license, overlay icons) already lives in
; %APPDATA%\CatFoil, independent of the install location, so an uninstall leaves
; it untouched and a reinstall/upgrade keeps every setting.
;
; Microsoft Store readiness (MSI/EXE submission path): the payload is bundled
; (offline install) and Inno supports silent install (/VERYSILENT) — both are
; Store requirements. Before submitting, the setup EXE and the bundled CatFoil.exe
; must be code-signed with a cert chaining to a Microsoft-Trusted-Root CA
; (add a [Setup] SignTool= directive + sign the payload during publish).

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#define MyAppName "CatFoil"
#define MyAppPublisher "Skordio"
#define MyAppURL "https://github.com/Skordio/CatFoil"
#define MyAppExeName "CatFoil.exe"

[Setup]
; Stable, never change — identifies the app for upgrades/uninstall.
AppId={{89260DC4-E954-43A2-983E-240E1BB91AE2}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion}
; {autopf} resolves to %LOCALAPPDATA%\Programs in non-admin install mode.
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=CatFoil-Setup-{#MyAppVersion}
SetupIconFile=..\assets\cat.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Detect a running CatFoil via its single-instance mutex and offer to close it,
; so the installer can replace the (self-locking) EXE without a reboot.
AppMutex=CatFoil-SingleInstance
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
