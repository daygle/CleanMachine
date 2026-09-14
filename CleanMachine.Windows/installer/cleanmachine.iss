; CleanMachine Inno Setup script
; Requires Inno Setup 6.x (https://jrsoftware.org/isinfo.php)
; Run with: iscc cleanmachine.iss

#define MyAppName "CleanMachine"
; Version comes from the release workflow via ISCC /DAppVersion=x.y.z;
; the /D syntax sets a preprocessor define whose value is substituted with {#AppVersion}.
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
; The app can run in the system tray with a hidden window and a background
; agent, so Setup must close it before replacing files. [Code] below performs a
; graceful shutdown (tray icon and background agent included); this directive is
; the built-in install-time safety net that force-closes anything still holding
; our files (e.g. an instance in another user session) without prompting. The
; uninstaller does not use Restart Manager, which is why its own code path
; exists below.
CloseApplications=force
; Prevent Restart Manager from silently relaunching the app after an update;
; the [Run] entry already offers a normal launch.
RestartApplications=no
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Installer for {#MyAppName}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Published (self-contained) app output. -p:Platform=x64 puts it under bin\x64\Release.
Source: "..\bin\x64\Release\net8.0-windows10.0.26100.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  AppExeName = 'CleanMachine.exe';
  AppMutexName = 'Local\CleanMachine.SingleInstance';

// True when a CleanMachine instance is alive (via the single-instance mutex).
// Defined above ShutdownApplication: Inno's Pascal Script resolves identifiers
// top-down, so a function cannot call one declared later in the script.
function AppIsRunning: Boolean;
begin
  Result := CheckForMutexes(AppMutexName);
end;

// Asks a running CleanMachine to exit gracefully. Attempts, in order:
//   1. Launch the app itself with --shutdown: the running instance is told via a
//      named event to exit exactly like the tray menu's Exit (background agent,
//      tray icon, and window all shut down cleanly), and the helper falls back
//      to killing the process if it will not exit in time.
//   2. taskkill: a safety net for a hung instance.
procedure ShutdownApplication;
var
  ResultCode: Integer;
begin
  Log('Closing a running CleanMachine before continuing');
  // Graceful path: only newer versions create the single-instance mutex and
  // understand --shutdown, so gate the helper on the mutex. When it succeeds the
  // instance exits exactly like the tray menu's Exit (clean agent + tray icon).
  if AppIsRunning then
  begin
    if Exec(ExpandConstant('{app}\' + AppExeName), '--shutdown', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      Log('--shutdown helper finished with exit code ' + IntToStr(ResultCode))
    else
      Log('--shutdown helper could not be launched');
  end;
  // Safety net (also the only path for older versions that predate the mutex,
  // and for hung instances): force-close by image name. Exit code 128 means
  // nothing was running, which is fine.
  if Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM ' + AppExeName + ' /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log('taskkill exit code ' + IntToStr(ResultCode));
end;

// Install (including the silent self-update launched by the app): close the
// running instance before files are replaced.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := ''; // an empty string means: continue
  // Unconditional so upgrades from versions without the mutex are covered too.
  ShutdownApplication;
end;

// Uninstall: the reported bug - the uninstaller never closed the app, so files
// stayed locked and (when it sat in the tray) CleanMachine kept running after
// being "removed".
function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True; // never block the uninstall; just close the app first
  // Unconditional: older versions do not create the single-instance mutex, so a
  // mutex check alone would miss an instance sitting in the tray and the bug
  // would persist for existing installs. ShutdownApplication is a cheap no-op
  // when nothing is running.
  ShutdownApplication;
  // The app registers "Run at startup" when a background service is enabled;
  // remove it so a removed copy is not launched at the next logon. reg.exe runs
  // under the original user's token, so this deletes that user's HKCU value even
  // when the uninstaller itself was elevated.
  ExecAsOriginalUser(ExpandConstant('{sys}\reg.exe'),
    'delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v CleanMachine /f',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // Remove scheduled cleanup tasks (folder \CleanMachine\). PowerShell because
  // schtasks cannot enumerate/delete by task folder.
  ExecAsOriginalUser(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -Command "Get-ScheduledTask -TaskPath ''\CleanMachine\'' -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
