# CleanMachine

CleanMachine is a native Windows 10/11 desktop application scaffolded with **C#/.NET 8 and WinUI 3**.

## Project

- `CleanMachine.Windows/` — native WinUI desktop application
- Dedicated pages for Overview, Cleaner, Registry Care, Windows Cleanup, Secure Delete, Activity, Settings, and Updates
- Safe browser-cache and Windows-cleanup review workflows
- Read-only Registry Care with `.reg` backup/restore helpers
- Explicit-file Secure Delete with selectable wipe methods
- Architecture-aware update manifest and hash verification

## Safety model

All destructive workflows are review-first. Browser cleaning is limited to recreatable cache directories and requires supported browsers to be closed. Cookies, passwords, bookmarks, history, and sessions are not targeted. Windows Cleanup only executes `Safe` categories without additional confirmation and rejects protected, recently modified, locked, inaccessible, and reparse-point paths. Recycle Bin cleanup is available only after explicit confirmation and reports Windows API failures. Windows Update cleanup requires explicit confirmation plus administrator elevation, but remains disabled until a safe Windows service/API implementation is validated.

Registry Care scans read-only and does not delete registry entries. Selected high-confidence low-risk findings can produce a real current-user uninstall-key `.reg` export; restore is explicit and uses Windows `reg.exe`. Registry mutation remains disabled pending Windows backup/restore and rollback testing.

Secure Delete operates only on explicitly selected ordinary files after review. It supports Simple zero-fill (1 pass), US DoD 5220.22-M (3 passes), US DoD 5220.22-M ECE (7 passes), Peter Gutmann (35 passes), and Custom (1–35 passes). These are compatibility labels, not guarantees of forensic erasure; overwrite is not reliable sanitization for SSDs or modern storage.

## Build and test on Windows

Install Visual Studio 2022 with .NET Desktop Development, Windows App SDK/WinUI, and the Windows 10/11 SDK. Then run:

```powershell
dotnet restore CleanMachine.Windows/CleanMachine.Windows.csproj
dotnet build CleanMachine.Windows/CleanMachine.Windows.csproj -p:Platform=x64
dotnet test CleanMachine.Windows.Tests/CleanMachine.Windows.Tests.csproj -p:Platform=x64
```

The current environment cannot compile or run WinUI/XAML, exercise Windows registry permissions, validate browser profile locks, create MSIX packages, or test Authenticode signatures.

## Updates and releases

`UpdateService.cs` checks the HTTPS multi-architecture release manifest, selects the current architecture, validates semantic version/package metadata, verifies SHA-256, and requires publisher validation for MSIX packages. Update state is persisted atomically so interrupted staged/installing operations can be recovered. Release signing still requires a real Authenticode certificate configured as a GitHub Actions secret; no credentials belong in this repository.

`.github/workflows/release-windows.yml` publishes x64 and ARM64 archives, hashes, and `update-manifest.json` for tags such as `v1.0.1`. Before production use, configure MSIX packaging/signing and validate a real tagged release end-to-end.
