# CleanMachine

CleanMachine is a native Windows 10/11 desktop application scaffolded with **C#/.NET 8 and WinUI 3**.

## Project

- `CleanMachine.Windows/` - native WinUI desktop application
- Dedicated pages for Overview, Cleaner, Registry Care, Windows Cleanup, Secure Delete, Activity, Settings, and Updates
- Safe browser-cache and Windows-cleanup review workflows
- Read-only Registry Care with `.reg` backup/restore helpers
- Explicit-file Secure Delete with selectable wipe methods
- Architecture-aware update manifest and signed MSIX validation

## Features

### Browser Cleaner
- Scans Chrome, Edge, and Firefox profiles including standard, custom, and portable installations
- Multi-profile discovery across local and roaming application data
- Configurable exclusion paths to skip specific directories
- Interrupted-cleanup state persistence with recovery messaging
- Process-lock detection requires browsers to be closed before cleaning
- Detects Chrome, Edge, Brave, Opera, Vivaldi, Firefox, and Internet Explorer and shows them as per-browser cards
- Per-item selection: safe items (cache, sessions, crash reports, metrics, bookmark backups) are on by default; destructive items (cookies, history, downloads, autofill, saved passwords) are opt-in behind a confirmation
- Whole-file deletion, with a backup taken before any preference-file edit

### Registry Care
- Read-only scanning of current-user uninstall metadata, file associations, MUI cache, startup entries, and orphaned sound events
- Safe per-user cleanup with value-level deletion, so shared keys are never removed wholesale
- Confidence-based filtering (minimum 70%) for review eligibility
- `.reg` backup export with validation of backup header integrity; cleaning refuses to run without a backup
- Explicit restore flow using Windows `reg.exe`

### Windows Cleanup
- Safe category scanning: user temporary files, thumbnail cache, error reports
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

### Updates and Releases
- HTTPS-only MSIX package validation
- Architecture-specific package selection (x64, ARM64)
- SHA-256 hash verification
- Authenticode publisher verification
- Atomic update state transitions (staged → installing → installed)
- Rollback copy staging and executable restoration after failed installation
- Pending-update recovery across sessions

### Scheduled Cleanup
- Recurring cleanup on Daily / Weekly / Monthly schedules or at logon, registered with Windows Task Scheduler so it runs even while the app is closed
- Per-schedule item selection across Windows cleanup categories, browser caches, and Registry Care categories
- Optional post-clean action - notify, shut down, restart, or sleep - with a 60-second abort window for shutdown and restart
- Runs with least privilege, so only per-user items are touched

### Settings
- Background agent startup toggle
- Minimize to tray (keeps the taskbar button)
- Automatic cleanup on browser exit toggle
- Update check frequency toggle
- Default wipe method selection
- Configurable exclusion paths
- Persisted startup registration

## Safety model

All destructive workflows are review-first. Browser cleaning requires supported browsers to be closed; safe items (caches, sessions, crash reports) are selected by default, while destructive items (cookies, history, saved passwords) are opt-in behind a confirmation. Registry Care deletes only after a verified `.reg` backup and only from an allow-listed set of per-user paths. Windows Cleanup rejects protected, recently modified, locked, inaccessible, and reparse-point paths. Recycle Bin cleanup requires explicit confirmation. Windows Update cleanup remains disabled until a safe Windows service/API implementation is validated.

Registry Care scans read-only and does not delete registry entries. Selected high-confidence low-risk findings can produce a real current-user uninstall-key `.reg` export; restore is explicit and uses Windows `reg.exe`.

Secure Delete operates only on explicitly selected ordinary files after review. It supports Simple zero-fill (1 pass), US DoD 5220.22-M (3 passes), US DoD 5220.22-M ECE (7 passes), Peter Gutmann (35 passes), and Custom (1–35 passes). These are compatibility labels, not guarantees of forensic erasure; overwrite is not reliable sanitization for SSDs or modern storage.

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
