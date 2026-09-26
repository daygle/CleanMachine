# CleanMachine

CleanMachine is a native Windows 10/11 desktop application built with **C#/.NET 10 and WinUI 3** (Windows App SDK 2.5).

## Project

- `CleanMachine.Windows/` - native WinUI desktop application
- Dedicated pages for Overview, Browser Cleaner, Registry Care, Backups, Windows Cleanup, Application Cleanup, Installed Apps, Startup Apps, Activity, Schedules, Automatic Cleanup, and Settings
- Safe browser-cache and Windows-cleanup review workflows
- Read-only Registry Care with `.reg` backup/restore helpers
- Startup program management and an installed-apps viewer/uninstaller launcher
- One-click "Clean All Safe Items" with a single confirmation
- Persistent cleanup statistics and a live per-area availability dashboard
- Distributed and updated exclusively through the Microsoft Store (no in-app updater)

## Features

### Overview
- Lifetime and 30-day cleanup stats: items cleaned, space recovered, last cleanup time
- Live availability cards per area (Browser Cleaner, Windows Cleanup, Registry Care, Application Cleanup) showing what could be cleaned right now; results are cached for a few minutes so revisiting is instant, and any cleanup invalidates the cache
- "Clean All Safe Items" runs every area's safe cleanables in sequence behind one confirmation, with per-area figures, progress, and cancellation
- Each area's Quick Clean has a gear picker choosing exactly what it includes; the Windows picker also offers the Recycle Bin as an explicit opt-in (off by default, since it permanently removes deleted items)

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
- Same availability figure feeds the Overview dashboard

### Registry Care
- Read-only scanning of current-user (HKCU) entries only - leftover uninstall metadata (no removal command, or an uninstaller that is missing), dangling file associations (a missing handler class, or one whose open command runs a program that is gone), "Open with" entries referencing a missing handler or program, MUI cache, startup entries, orphaned sound events, Shell app-name cache (MuiCache) for missing programs, App Paths entries pointing at missing programs, and Compatibility Assistant records for missing programs. Machine-wide (HKLM) keys are never touched, since editing them needs elevation and is far riskier
- Two-pane list/detail view: finding categories as expanders on the left, a summary header with eligible/selected/total chips on the right, and a per-finding drill-down showing its registry key, value, confidence and reason
- Safe per-user cleanup with value-level deletion, so shared keys are never removed wholesale
- Confidence-based filtering (minimum 70%) for review eligibility; Show All reveals ineligible findings when there are any (and is disabled with an explanation when there are none)
- `.reg` backup export with validation of backup header integrity; cleaning refuses to run without a backup
- A Backups link showing how many backup files exist and opening the backup folder in Explorer
- Explicit restore flow using Windows `reg.exe`; the list re-scans after a clean

### Windows Cleanup
- Two-pane list/detail view: categories on the left, a summary header with chips and a per-category file drill-down on the right
- Safe category scanning: temporary files, thumbnail and icon caches, error reports, internet cache, Remote Desktop cache, PowerShell history, GPU shader caches (NVIDIA/AMD/Intel), custom jump lists, Microsoft Store cache, certificate revocation cache (CryptnetUrlCache), Windows Spotlight image cache, and more; plus an Advanced, off-by-default Windows Update download cache
- Review-tier (off by default, confirm first): Recycle Bin, downloaded files by type, and Action Center notification history
- History/MRU categories (Run, Search, Open/Save dialog, Recent, jump lists) clear recursively through sub-keys, so entries stored inside sub-keys (e.g. the Open/Save dialog MRUs) are actually removed
- Categories with nothing to clean are hidden from the selection list (shared, cached scan with the Overview card); if a scan fails the full catalog is shown so nothing becomes unreachable
- Per-category enable/disable persisted in settings, honoring per-user overrides; the list re-measures after a clean
- Recycle Bin cleanup through native `SHEmptyRecycleBin` (requires explicit confirmation)
- Reparse-point and junction protection
- Category-specific exclusion support
- Progress and cancellation handling

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
- Optional post-clean action - notify, shut down, restart, or sleep - with a 60-second abort window for shutdown and restart
- Runs with least privilege, so only per-user items are touched

### Automatic Cleanup
- A single page for all hands-off cleaning; the background agent has no switch of its own - it runs (and CleanMachine registers to start with Windows) automatically whenever any option here needs it, and stops when the last one is turned off:
  - Browser-exit monitoring: cleans a monitored browser's cache when it closes, with a per-browser action and item picker (destructive items opt-in)
  - Low-disk monitoring: when free space on the Windows drive drops below a threshold - set in MB or GB - cleans a chosen set of Safe-risk categories (or all enabled ones by default) at most once per hour, re-arming after free space recovers
  - At startup: run a clean each time CleanMachine starts (with "Start with Windows", that is every logon)
  - On idle: run a clean after the PC has been idle a configurable number of minutes, once per idle period
  - Recycle Bin: automatically empty items older than a configurable number of days (only ever removes items already in the Recycle Bin)
- The startup, idle, and Recycle Bin triggers each have a "show a notification when this runs" checkbox (on by default on a fresh install); browser-exit and low-disk monitoring instead choose Do nothing / Clean silently / Clean and notify per trigger (clean and notify by default)
- The low-disk, startup, and idle triggers each have their own independent category selection (defaulting to every category enabled on the Windows Cleanup page); automatic runs only ever offer the cleanup categories, never Review/Advanced ones
- Every automated run records into the same stats store and activity log as manual cleans

### Settings
- Settings save instantly on change - there is no Save button (Restore Defaults applies immediately too)
- System monitoring threshold (entered in MB or GB), action, and a picker for exactly which safe categories the monitor cleans
- "Start CleanMachine when I sign in to Windows" toggle, independent of the background services; a logon start opens straight to the tray
- Minimize to tray options (start minimized, on close, on minimize; taskbar visibility)
- Tray icon: on by default for the whole session, even while the window is open (toggle in Settings); left-click restores the window; right-click opens a menu to Open or Exit CleanMachine
- Configurable exclusion paths
- Persisted startup registration
- All automatic/background cleaning preferences live on the Automatic Cleanup page

## Safety model

All destructive workflows are review-first. Browser cleaning requires supported browsers to be closed; safe items (caches, sessions, crash reports) are selected by default, while destructive items (cookies, history, saved passwords) are opt-in behind a confirmation. Registry Care deletes only after a verified `.reg` backup and only from an allow-listed set of per-user paths. Windows Cleanup rejects protected, recently modified, locked, inaccessible, and reparse-point paths. Recycle Bin cleanup requires explicit confirmation. CleanMachine does not modify the protected Windows component store.

"Clean All Safe Items" is bounded the same way: only non-destructive browser cache items, enabled Safe-risk Windows categories, application temp files, and registry findings passing the safety gate (with a mandatory backup) are included. Downloads, documents, Review/Advanced categories, and destructive browser items are never touched by it. Quick Clean is bounded the same way, with one deliberate exception: the Recycle Bin can be opted into per user choice in its picker, and ticking it there is the confirmation its Review risk requires.

Startup Apps changes affect only auto-start entries, never the programs themselves. Installed Apps uninstalls run the vendor's own uninstaller; CleanMachine does not delete other programs' files.

Registry Care scans read-only and does not delete registry entries. Selected high-confidence low-risk findings can produce a real current-user uninstall-key `.reg` export; restore is explicit and uses Windows `reg.exe`.

CleanMachine ships no secure-erase tool and no drive wiper. Those were removed
because they are the features most likely to draw certification scrutiny on a
public Store listing, and because overwrite is not reliable sanitization on SSDs
or modern storage in the first place.

## Build and test on Windows

Install the .NET 10 SDK (e.g. `winget install Microsoft.DotNet.SDK.10`) and the Windows App
Runtime 1.6+ (installed automatically on first run if missing). Then run:

```powershell
dotnet restore CleanMachine.Windows/CleanMachine.Windows.csproj
dotnet build CleanMachine.Windows/CleanMachine.Windows.csproj -p:Platform=x64
dotnet test CleanMachine.Windows.Tests/CleanMachine.Windows.Tests.csproj -p:Platform=x64
```

The app is self-contained for the Windows App SDK
(`WindowsAppSDKSelfContained=true`), so it runs without a separate runtime
install. The app targets the Windows SDK 10.0.26100; adjust `TargetFramework` if building
on a machine with an older SDK installed.

## Distribution

CleanMachine is distributed **only through the Microsoft Store**. There is no
self-signed sideload package, no `.exe` installer, and no in-app updater: the
Store installs the app, signs it with Microsoft's own certificate, and delivers
updates. Because the Store signs submissions itself, **publishing on the Store
does not require purchasing a code-signing certificate** - see
[RELEASE.md](RELEASE.md) for the submission process and the `runFullTrust`
capability justification.

### Uninstalling and your data

CleanMachine keeps its per-user data in `%LOCALAPPDATA%\CleanMachine` (settings, cleanup
statistics, activity history, and Registry Care `.reg` backups) so a
reinstall picks up where you left off. Uninstalling from **Settings > Uninstall
CleanMachine...** inside the app closes the app, removes the
startup entry and scheduled cleanup tasks, sweeps away a desktop shortcut the app may
have created itself, and then asks whether to also delete that data folder; the
default answer is **No** so an accidental uninstall never destroys your history.
Choose **Yes** for a clean slate.

The **MSIX** build gets the same treatment from **Settings > Uninstall CleanMachine...**
inside the app: Windows' own MSIX uninstall cannot ask first and used to leave the desktop
shortcut behind (a package has no uninstall hook), so the in-app flow confirms, offers the
same keep/delete choice for the data (keeping is the default), removes the desktop
shortcut, scheduled cleanup tasks, and startup entry, and then removes the package.
App data lives in the same `%LOCALAPPDATA%\CleanMachine` folder, so keeping the data means a
reinstall picks up where you left off - and uninstalling from Windows Settings keeps it too.
