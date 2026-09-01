# CleanMachine

CleanMachine is a native Windows 10/11 desktop application scaffolded with **C#/.NET 8 and WinUI 3**.

## Project

- `CleanMachine.Windows/` — native WinUI desktop application
- Dedicated pages for Overview, Cleaner, Registry Care, Windows Cleanup, Secure Delete, Activity, Settings, and Updates
- Safe browser-cache and Windows-cleanup review workflows
- Read-only Registry Care with `.reg` backup/restore helpers
- Explicit-file Secure Delete with selectable wipe methods
- Architecture-aware update manifest and signed MSIX validation

## Safety model

All destructive workflows are review-first. Browser cleaning is limited to recreatable cache directories and requires supported browsers to be closed. Cookies, passwords, bookmarks, history, and sessions are not targeted. Windows Cleanup rejects protected, recently modified, locked, inaccessible, and reparse-point paths. Recycle Bin cleanup requires explicit confirmation. Windows Update cleanup remains disabled until a safe Windows service/API implementation is validated.

Registry Care scans read-only and does not delete registry entries. Selected high-confidence low-risk findings can produce a real current-user uninstall-key `.reg` export; restore is explicit and uses Windows `reg.exe`.

Secure Delete operates only on explicitly selected ordinary files after review. It supports Simple zero-fill (1 pass), US DoD 5220.22-M (3 passes), US DoD 5220.22-M ECE (7 passes), Peter Gutmann (35 passes), and Custom (1–35 passes). These are compatibility labels, not guarantees of forensic erasure; overwrite is not reliable sanitization for SSDs or modern storage.

## Build and test on Windows

Install Visual Studio 2022 with .NET Desktop Development, Windows App SDK/WinUI, and the Windows 10/11 SDK. Then run:

```powershell
dotnet restore CleanMachine.Windows/CleanMachine.Windows.csproj
dotnet build CleanMachine.Windows/CleanMachine.Windows.csproj -p:Platform=x64
dotnet test CleanMachine.Windows.Tests/CleanMachine.Windows.Tests.csproj -p:Platform=x64
```

The current environment cannot compile or run WinUI/XAML, exercise Windows registry permissions, validate browser profile locks, create MSIX packages, or test Authenticode signatures.

## Updates and signed releases

`UpdateService.cs` accepts only HTTPS `.msix` packages for the current architecture. It validates semantic versions, SHA-256 hashes, required publisher metadata, and the embedded package certificate before installation. It uses the Windows App SDK deployment manager for MSIX installation, persists staged/installing state atomically, stages a rollback copy, and can restore the previous executable after a failed installation.

The release workflow builds architecture-specific MSIX packages and hashes, validates Authenticode signatures and the configured publisher, and publishes a multi-architecture `update-manifest.json`.

Configure these GitHub repository settings before creating a production tag:

- Repository variable `WINDOWS_PUBLISHER`: exact expected certificate subject/publisher string
- Repository secret `WINDOWS_SIGNING_CERTIFICATE_BASE64`: base64-encoded PFX certificate
- Repository secret `WINDOWS_SIGNING_CERTIFICATE_PASSWORD`: PFX password

Do not commit certificates, passwords, or private keys. The workflow intentionally fails tagged releases when signing configuration is absent. Create a test tag such as `v0.1.1` only after configuring the secrets, then verify the release assets, `Get-AuthenticodeSignature` output, SHA-256 files, and manifest URLs on a Windows runner.
