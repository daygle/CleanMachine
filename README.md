# CleanMachine

CleanMachine is a native Windows 10/11 desktop application scaffolded with **C#/.NET 8 and WinUI 3**.

## Project

- `CleanMachine.Windows/` - native WinUI desktop application
- Dedicated pages for Overview, Browser Cleaner, Registry Care, Windows Cleanup, Application Cleanup, Secure Delete, Drive Wiper, Startup Apps, Installed Apps, Activity, Schedules, Automatic Cleanup, Settings, and Updates
- Safe browser-cache and Windows-cleanup review workflows
- Read-only Registry Care with `.reg` backup/restore helpers
- Explicit-file Secure Delete with selectable wipe methods
- CCleaner-style free-space Drive Wiper
- Startup program management and an installed-apps viewer/uninstaller launcher
- One-click "Clean All Safe Items" with a single confirmation
- Persistent cleanup statistics and a live per-area availability dashboard
- Architecture-aware update manifest and signed MSIX validation

## Features

### Overview
- Lifetime and 30-day cleanup stats: items cleaned, space recovered, last cleanup time
- Live availability cards per area (Browser Cleaner, Windows Cleanup, Registry Care, Application Cleanup) showing what could be cleaned right now; results are cached for a few minutes so revisiting is instant, and any cleanup invalidates the cache
- Update status card: installed version, automatic check (debounced), manual check, and update details when a new version exists
- "Clean All Safe Items" runs every area's safe cleanables in sequence behind one confirmation, with per-area figures, progress, and cancellation

### Browser Cleaner
- Scans Chrome, Edge, and Firefox profiles including standard, custom, and portable installations
- Multi-profile discovery across local and roaming application data
- Configurable exclusion paths to skip specific directories
- Interrupted-cleanup state persistence with recovery messaging
- Process-lock detection requires browsers to be closed before cleaning
- Detects Chrome, Edge, Brave, Opera, Vivaldi, Firefox, and Internet Explorer in a two-pane list/detail view: browsers (with their items) on the left, a summary header, size/file/item chips, and a per-item drill-down on the right
- Per-item selection: safe items (cache, sessions, crash reports, metrics, bookmark backups) are on by default; destructive items (cookies, history, downloads, autofill, saved passwords) are opt-in behind a confirmation. Your tick choices are remembered across navigation and restarts
- The list re-scans after a clean so sizes reflect what was removed
- Whole-file deletion, with a backup taken before any preference-file edit
- Browser monitoring lives here: choose what happens when each supported browser closes (do nothing, clean silently, clean and notify), with a master on/off switch
- Enabling any Automatic Cleanup option automatically runs the background agent and registers CleanMachine to start with Windows, so those services work without the window open; there is no separate agent switch to remember

### Application Cleanup
- Two-pane list/detail view: detected apps (grouped by Desktop / Microsoft Store) on the left, a summary header with size/file/item chips and a per-item file drill-down on the right
- Shows only apps with items by default (clean apps hidden, or shown greyed out behind Show All)
- Broad built-in catalog of desktop apps (Chrome, Edge, Brave, Vivaldi, Opera, Discord, Slack, Signal, Spotify, Teams, VS Code, JetBrains IDEs, Postman, Steam, Epic Games, Zoom, Office, Thunderbird, Adobe Acrobat, Adobe media cache and more) and Microsoft Store apps (Teams, Outlook, Phone Link, Mail and Calendar, Maps, Camera, Xbox, WhatsApp, Netflix, Photos, Solitaire and more), plus Windows components (Defender logs, search index, media player caches, activity history)
- Temp-file locations support wildcard path segments, so apps that store caches under randomly-named or versioned per-profile folders (e.g. Thunderbird profiles, JetBrains product/version folders) are matched correctly
- The list re-scans after a clean so sizes reflect what was removed
- Secure Delete option uses the wipe method from Settings
- Same availability figure feeds the Overview dashboard

### Registry Care
- Read-only scanning of current-user (HKCU) entries only - leftover uninstall metadata (no removal command, or an uninstaller that is missing), dangling file associations (a missing handler class, or one whose open command runs a program that is gone), "Open with" entries referencing a missing handler or program, MUI cache, startup entries, orphaned sound events, Shell app-name cache (MuiCache) for missing programs, App Paths entries pointing at missing programs, and Compatibility Assistant records for missing programs. Machine-wide (HKLM) keys are never touched, since editing them needs elevation and is far riskier
- Two-pane list/detail view: finding categories as expanders on the left, a summary header with eligible/selected/total chips on the right, and a per-finding drill-down showing its registry key, value, confidence and reason
- Safe per-user cleanup with value-level deletion, so shared keys are never removed wholesale
- Confidence-based filtering (minimum 70%) for review eligibility; Show All reveals ineligible findings when there are any (and is disabled with an explanation when there are none)
- `.reg` backup export with validation of backup header integrity; cleaning refuses to run without a backup
- Explicit restore flow using Windows `reg.exe`; the list re-scans after a clean

### Windows Cleanup
- Two-pane list/detail view: categories on the left, a summary header with chips and a per-category file drill-down on the right
- Safe category scanning: temporary files, thumbnail and icon caches, error reports, internet cache, Remote Desktop cache, PowerShell history, GPU shader caches (NVIDIA/AMD/Intel), custom jump lists, Microsoft Store cache, certificate revocation cache (CryptnetUrlCache), Windows Spotlight image cache, and more; plus an Advanced, off-by-default Windows Update download cache
- Review-tier (off by default, confirm first): Recycle Bin, downloaded files by type, and Action Center notification history
- History/MRU categories (Run, Search, Open/Save dialog, Recent, jump lists) clear recursively through sub-keys, so entries stored inside sub-keys (e.g. the Open/Save dialog MRUs) are actually removed
- Categories with nothing to clean are hidden from the selection list (shared, cached scan with the Overview card); if a scan fails the full catalog is shown so nothing becomes unreachable
- Per-category enable/disable persisted in settings, honoring per-user overrides; the list re-measures after a clean
- Recycle Bin cleanup through native `SHEmptyRecycleBin` (requires explicit confirmation)
- Windows Update cleanup disabled until safe API/service implementation is validated
- Reparse-point and junction protection
- Category-specific exclusion support
- Progress and cancellation handling

### Secure Delete
- Native file picker for selecting explicit files
- File eligibility review before deletion
- Protected/read-only file detection
- SSD acknowledgement requirement
- Multi-pass overwrite: Simple zero-fill, US DoD 5220.22-M, ECE, Peter Gutmann, Custom
- Progress bar, cancellation, and post-overwrite verification

### Drive Wiper
- CCleaner-style free-space wipe: overwrites free clusters by writing a temporary wiper file (always cleaned up, including from interrupted runs)
- Drive picker for fixed internal drives, 1/3/7-pass options, live progress and cancellation
- Optional filesystem-metadata wipe that a free-cluster pass misses: overwrite free NTFS MFT records (where small deleted files can linger resident) or freed FAT/exFAT directory entries; each option is enabled only for the matching filesystem of the selected drive, and works by briefly creating and deleting many small temp files
- Wiper file lands in `Users\Public` on non-admin accounts since `C:\` root is not writable without elevation
- SSD caveat surfaced in the UI: wear leveling means free-space wiping cannot guarantee sanitization there

### Startup Apps
- Enumerates HKCU/HKLM Run and RunOnce values plus per-user and common startup folders, grouped by location
- Enable/disable uses the same Explorer `StartupApproved` convention as Task Manager (no elevation needed, state visible in both places)
- Remove deletes only the auto-start entry; all-users entries are refused with a clear message since they need elevation

### Installed Apps
- Lists Win32 apps from the standard uninstall registry keys (both 64-bit and 32-bit registry views) plus Store packages, with version, publisher, size, and install date
- Search across name, publisher, and version
- Uninstall and Modify launch the vendor's own uninstaller/change program; Store packages uninstall through the deployment API with a standard confirmation
- CleanMachine never deletes another program's files itself

### Activity
- Chronological log of automated cleanups: browser-exit and system monitoring, scheduled runs, and Clean All, with items removed and space recovered (individual manual page runs record into the Overview stats)

### Scheduled Cleanup
- Recurring cleanup on Daily / Weekly / Monthly schedules or at logon, registered with Windows Task Scheduler so it runs even while the app is closed
- Per-schedule item selection across browser caches, application temp files, Windows cleanup categories, and Registry Care categories
- Optional secure delete using the method from Settings
- Optional post-clean action - notify, shut down, restart, or sleep - with a 60-second abort window for shutdown and restart
- Runs with least privilege, so only per-user items are touched

### Automatic Cleanup
- A single page for all hands-off cleaning; the background agent has no switch of its own - it runs (and CleanMachine registers to start with Windows) automatically whenever any option here needs it, and stops when the last one is turned off:
  - Browser-exit monitoring: cleans a monitored browser's cache when it closes, with a per-browser action and item picker (destructive items opt-in)
  - Low-disk monitoring: when free space on the Windows drive drops below a threshold - set in MB or GB - cleans a chosen set of Safe-risk categories (or all enabled ones by default) at most once per hour, re-arming after free space recovers
  - At startup: run a clean each time CleanMachine starts (with "Start with Windows", that is every logon)
  - On idle: run a clean after the PC has been idle a configurable number of minutes, once per idle period
  - Recycle Bin: automatically empty items older than a configurable number of days (only ever removes items already in the Recycle Bin)
- The low-disk, startup, and idle triggers each have their own independent category selection (defaulting to every category enabled on the Windows Cleanup page); automatic runs only ever offer the cleanup categories, never Review/Advanced ones
- Every automated run records into the same stats store and activity log as manual cleans

### Updates and Releases
- HTTPS-only MSIX package validation
- Architecture-specific package selection (x64, ARM64)
- SHA-256 hash verification
- Authenticode publisher verification
- Atomic update state transitions (staged -> installing -> installed)
- Rollback copy staging and executable restoration after failed installation
- Pending-update recovery across sessions

### Settings
- Settings save instantly on change - there is no Save button (Restore Defaults applies immediately too)
- System monitoring threshold (entered in MB or GB), action, and a picker for exactly which safe categories the monitor cleans
- Automatic update check toggle, and an option to install updates without CleanMachine's own confirmation step (Windows still shows its administrator-permission prompt)
- "Start CleanMachine when I sign in to Windows" toggle, independent of the background services; a logon start opens straight to the tray
- Minimize to tray options (start minimized, on close, on minimize; taskbar visibility)
- Tray icon: left-click restores the window; right-click opens a menu to Open or Exit CleanMachine
- Default wipe method selection
- Configurable exclusion paths
- Persisted startup registration
- All automatic/background cleaning preferences live on the Automatic Cleanup page

## Safety model

All destructive workflows are review-first. Browser cleaning requires supported browsers to be closed; safe items (caches, sessions, crash reports) are selected by default, while destructive items (cookies, history, saved passwords) are opt-in behind a confirmation. Registry Care deletes only after a verified `.reg` backup and only from an allow-listed set of per-user paths. Windows Cleanup rejects protected, recently modified, locked, inaccessible, and reparse-point paths. Recycle Bin cleanup requires explicit confirmation. Windows Update cleanup remains disabled until a safe Windows service/API implementation is validated.

"Clean All Safe Items" is bounded the same way: only non-destructive browser cache items, enabled Safe-risk Windows categories, application temp files, and registry findings passing the safety gate (with a mandatory backup) are included. Downloads, documents, Review/Advanced categories, and destructive browser items are never touched by it.

Startup Apps changes affect only auto-start entries, never the programs themselves. Installed Apps uninstalls run the vendor's own uninstaller; CleanMachine does not delete other programs' files. The Drive Wiper overwrites only free space and never touches existing files.

Registry Care scans read-only and does not delete registry entries. Selected high-confidence low-risk findings can produce a real current-user uninstall-key `.reg` export; restore is explicit and uses Windows `reg.exe`.

Secure Delete operates only on explicitly selected ordinary files after review. It supports Simple zero-fill (1 pass), US DoD 5220.22-M (3 passes), US DoD 5220.22-M ECE (7 passes), Peter Gutmann (35 passes), and Custom (1-35 passes). These are compatibility labels, not guarantees of forensic erasure; overwrite is not reliable sanitization for SSDs or modern storage.

## Build and test on Windows

Install the .NET 8 SDK (e.g. `winget install Microsoft.DotNet.SDK.8`) and the Windows App
Runtime 1.6+ (installed automatically on first run if missing). Then run:

```powershell
dotnet restore CleanMachine.Windows/CleanMachine.Windows.csproj
dotnet build CleanMachine.Windows/CleanMachine.Windows.csproj -p:Platform=x64
dotnet test CleanMachine.Windows.Tests/CleanMachine.Windows.Tests.csproj -p:Platform=x64
```

The unpackaged build is self-contained for the Windows App SDK
(`WindowsAppSDKSelfContained=true`), so `CleanMachine.exe` runs without a separate runtime
install. The app targets the Windows SDK 10.0.26100; adjust `TargetFramework` if building
on a machine with an older SDK installed.

## Updates and signed releases

`UpdateService.cs` accepts only HTTPS `.msix` packages for the current architecture. It validates semantic versions, SHA-256 hashes, required publisher metadata, and the embedded package certificate before installation. It uses the Windows App SDK deployment manager for MSIX installation, persists staged/installing state atomically, stages a rollback copy, and can restore the previous executable after a failed installation.

The release workflow builds architecture-specific MSIX packages and hashes, validates Authenticode signatures and the configured publisher, and publishes a multi-architecture `update-manifest.json`.

### SmartScreen ("Windows protected your PC")

Windows SmartScreen warns on downloaded executables that are unsigned or lack download
reputation. When the signing secrets below are configured, the workflow now signs the
app exe and the Inno Setup installer (SHA-256 with an RFC 3161 timestamp) in addition to
the MSIX; without them the installer is published unsigned and SmartScreen will warn
everyone who downloads it. Note that SmartScreen reputation is earned by downloads over
time, so newly signed releases can still show the warning at first; EV certificates get
immediate reputation. To install an unsigned or unreputable build anyway, click
"More info" -> "Run anyway" on the SmartScreen dialog.

The release workflow always produces a **signed** MSIX. If no production certificate is
configured it mints a throwaway **self-signed** code-signing certificate whose subject matches
the package publisher, so tagged releases succeed without a purchased certificate. Self-signed
packages require the user to trust the certificate before sideloading; they are not suitable for
unattended production distribution.

For production releases, configure these GitHub repository settings before creating a tag:

- Repository variable `WINDOWS_PUBLISHER`: exact expected certificate subject/publisher string
  (defaults to `CN=CleanMachine Publisher` when unset, which is also the self-signed subject)
- Repository secret `WINDOWS_SIGNING_CERTIFICATE_BASE64`: base64-encoded PFX certificate
- Repository secret `WINDOWS_SIGNING_CERTIFICATE_PASSWORD`: PFX password

When both secrets are present the workflow signs with the provided certificate instead of a
self-signed one. Do not commit certificates, passwords, or private keys. Create a test tag such
as `v0.1.1`, then verify the release assets, `Get-AuthenticodeSignature` output, SHA-256 files,
and manifest URLs on a Windows runner.
