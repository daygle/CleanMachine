; CleanMachine Inno Setup script
; Requires Inno Setup 6.x (https://jrsoftware.org/isinfo.php)
; Run with: iscc cleanmachine.iss

#define MyAppName "CleanMachine"
#define MyAppVersion "1.0.0"
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
OutputDir=..\..\bin\installer
OutputBaseFilename=CleanMachine-Setup-{#MyAppVersion}
SetupIconFile=CleanMachine.Windows\Assets\Square44x44Logo.png
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
Source: "..\bin\Release\net8.0-windows10.0.26100.0\win-x64\publish\CleanMachine.exe"; DestDir: "{app}"; Flags: ignoreversion
; Everything under the publish folder
Source: "..\bin\Release\net8.0-windows10.0.26100.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
