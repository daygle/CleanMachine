# CleanMachine

CleanMachine is now scaffolded as a native Windows 10/11 desktop application using **C#/.NET 8 and WinUI 3**.

## Project

- `CleanMachine.Windows/` — native WinUI desktop application
- `MainWindow.xaml` — dashboard UI
- `CleanupService.cs` — safe browser cleanup and registry scan foundation
- `WindowsCleanupService.cs` — safe Windows temporary-file scanning and cleanup
- `SecureDeleteService.cs` — opt-in named and custom overwrite-method foundation
- `App.xaml` — application startup

## Safety model

Registry work is read-only by default. The initial scanner only inspects the current user's uninstall metadata and returns findings for review; it does not modify or delete registry values. Browser cleanup is intentionally a no-op until explicit per-browser rules and backup/restore behavior are implemented. Windows Cleanup currently handles only stale `.tmp`, `.dmp`, and `.log` files in the user's temp directory; locked files are skipped. Recycle Bin and advanced system categories require explicit review and are not automatically selected. Secure Delete is opt-in, limited to explicitly selected ordinary files, and supports Simple zero-fill (1 pass), US DoD 5220.22-M (3 passes), US DoD 5220.22-M ECE (7 passes), Peter Gutmann (35 passes), and Custom (1–35 passes). These historical methods are compatibility labels, not guarantees of forensic erasure. The implementation skips files when media cannot be identified and warns that overwrite is not reliable SSD sanitization.

## Build on Windows

Install Visual Studio 2022 with:

- .NET Desktop Development
- Windows App SDK / WinUI workload
- Windows 10/11 SDK

Then run:

```powershell
dotnet restore CleanMachine.Windows/CleanMachine.Windows.csproj
dotnet build CleanMachine.Windows/CleanMachine.Windows.csproj -p:Platform=x64
```The existing React/Vite preview was replaced as the product implementation because browser code cannot access the Windows registry, system tray, browser processes, or user profile files.


## Prototype scope

The current native prototype focuses on demonstrating the product flow: dashboard navigation, cleanup review dialogs, status messaging, update checks, secure-delete method selection, persisted user settings, and bounded local activity history. Tray hosting, process-safe browser profile cleanup, full Windows category scanners, registry mutation/restore, signed MSIX packaging, and installer integration remain Windows-only release work.

## Updates

`UpdateService.cs` checks an HTTPS release manifest, compares semantic versions, downloads the package to a temporary directory, verifies its SHA-256 hash, and launches the verified installer. It does not clone the repository or execute branch contents. Replace the placeholder manifest URL in `UpdateService.cs` with the project's release URL before shipping. `update-manifest.example.json` documents the expected format.

`.github/workflows/release-windows.yml` builds self-contained x64 and ARM64 release artifacts when a version tag such as `v1.0.1` is pushed, then publishes them to a GitHub Release. Production releases should add MSIX packaging and Authenticode signing; the updater should verify the signed package before installation.