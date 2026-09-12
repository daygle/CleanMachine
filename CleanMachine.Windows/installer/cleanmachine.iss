; CleanMachine Inno Setup script
; Requires Inno Setup 6.x (https://jrsoftware.org/isinfo.php)
; Run with: iscc cleanmachine.iss

#define MyAppName "CleanMachine"
; Version comes from the release workflow via ISCC /DAppVersion=x.y.z;
# the /D syntax sets a preprocessor define whose value is substituted with {#AppVersion}.
#ifndef AppVersion
#define AppVersion "0.0.1"
#endif
#define MyAppVersion AppVersion
#define MyAppPublisher "CleanMachine contributors"
#define MyAppExeName "CleanMachine.exe"

[Setup]
AppId={{A3F1B7E2-4C8D-4E6A-9B5F-1C7D8E9F0A1B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
LicenseFile=..\..\LICENSE
; Uncomment and provide a cert+key to sign the installer:
; SignTool=signtool sign /d "{#MyAppName}" /fd sha256 /tr http://timestamp.digicert.com /td sha256 $f
; Output paths are relative to this script's folder (CleanMachine.Windows\installer\).
OutputDir=..\bin\installer
OutputBaseFilename=CleanMachine-Setup-{#MyAppVersion}
SetupIconFile=..\Assets\app.ico
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Installer for {#MyAppName}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Published (self-contained) app output. -p:Platform=x64 puts it under bin\x64\Release.
Source: "..\bin\x64\Release\net8.0-windows10.0.26100.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
