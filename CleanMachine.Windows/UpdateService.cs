using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Windows.ApplicationModel;

namespace CleanMachine.Windows;

public sealed record UpdatePackage(string PackageUrl, string Sha256, string Architecture, string? Publisher = null);
public sealed record UpdateManifest(string Version, string ReleaseNotes, string PackageUrl = "", string Sha256 = "", string Architecture = "", string? Publisher = null, Dictionary<string, UpdatePackage>? Packages = null, UpdatePackage? Installer = null);
public sealed record UpdateCheckResult(bool Available, UpdateManifest? Manifest, UpdatePackage? Package, string? Error);

public sealed class UpdateService
{
    private static readonly Uri ManifestUri = new("https://github.com/daygle/CleanMachine/releases/latest/download/update-manifest.json");

    /// <summary>True when running inside an MSIX package; false for standalone .exe installs.</summary>
    public static bool IsInstalledAsMsix { get; } = TryGetIsMsix();

    /// <summary>True when Windows Smart App Control is in enforcement mode (HKLM
    /// CI policy state 1). SAC blocks executables without cloud-verified reputation
    /// from starting - including freshly downloaded, correctly signed installers -
    /// and offers no "Run anyway" override, which surfaces as a Win32Exception at
    /// Process.Start with a message the user cannot act on.</summary>
    public static bool IsSmartAppControlEnforcing { get; } = QuerySmartAppControlEnforcing();

    private static bool QuerySmartAppControlEnforcing()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Policy");
            return key?.GetValue("VerifiedAndReputablePolicyState") is int state && state == 1;
        }
        catch { return false; }
    }

    private static bool TryGetIsMsix()
    {
        try { _ = Package.Current.Id.FullName; return true; }
        catch (Exception ex) when (ex is InvalidOperationException or COMException) { return false; }
    }
    // The published manifest is generated with camelCase keys (version, releaseNotes,
    // packages, ...). System.Text.Json is case-sensitive by default, so binding must
    // be case-insensitive for the record properties to populate.
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly UpdateStateStore _stateStore = new();

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = await _httpClient.GetStreamAsync(ManifestUri, cancellationToken);
            var manifest = await ParseManifestAsync(stream, cancellationToken);
            if (manifest is null || !Version.TryParse(manifest.Version, out _) || string.IsNullOrWhiteSpace(manifest.ReleaseNotes)) return new(false, null, null, "The release manifest was invalid.");
            var package = ResolvePackage(manifest);
            if (package is null || !IsValidPackage(package)) return new(false, null, null, "No signed update package is available for this device.");
            return new(IsNewer(manifest.Version), manifest, package, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return new(false, null, null, "Update check could not be completed."); }
    }

    internal static async Task<UpdateManifest?> ParseManifestAsync(Stream stream, CancellationToken cancellationToken = default)
        => await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, ManifestJsonOptions, cancellationToken: cancellationToken);

    /// <summary>Downloads the package, reporting download fraction (0..1) via
    /// <paramref name="progress"/>, then verifies it by SHA-256 (and MSIX publisher)
    /// before staging it. Verification is automatic - a failed hash/publisher check
    /// deletes the download and throws.</summary>
    public async Task<string> DownloadAndVerifyAsync(UpdatePackage package, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!IsValidPackage(package)) throw new InvalidOperationException("No signed update package is available for this device.");
        var isMsix = package.PackageUrl.EndsWith(".msix", StringComparison.OrdinalIgnoreCase);
        var ext = isMsix ? ".msix" : ".exe";
        var directory = Path.Combine(Path.GetTempPath(), "CleanMachine", "Updates"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"CleanMachine-{DateTime.UtcNow:yyyyMMddHHmmss}-{package.Architecture}{ext}");
        try
        {
            // ResponseHeadersRead so we get Content-Length up front and can stream with
            // progress; the body read is not bound by HttpClient.Timeout.
            using (var response = await _httpClient.GetAsync(new Uri(package.PackageUrl), HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    copied += read;
                    if (total is > 0) progress?.Report(Math.Min(1.0, (double)copied / total.Value));
                }
            }
            progress?.Report(1.0);
            await using var verify = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken));
            if (!hash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The downloaded package failed hash verification.");
            if (!HasExpectedPublisher(path, package.Publisher)) throw new InvalidDataException("The downloaded update failed publisher verification.");
            await _stateStore.MarkAsync(
                "staged", path, null, cancellationToken,
                expectedSha256: package.Sha256,
                expectedPublisher: package.Publisher);
            return path;
        }
        catch { TryDelete(path); throw; }
    }

    public async Task InstallVerifiedPackageAsync(string packagePath, string currentExecutable, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(packagePath)) throw new FileNotFoundException("Staged package not found.", packagePath);
        var stagedState = await _stateStore.LoadAsync(cancellationToken);
        var expectedHash = stagedState?.ExpectedSha256;
        if (stagedState is not { Status: "staged" or "installing" }
            || !string.Equals(stagedState.PackagePath, packagePath, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(expectedHash)
            || expectedHash.Length != 64)
            throw new InvalidDataException("The staged update could not be authenticated.");

        await using (var verifyStream = File.OpenRead(packagePath))
        {
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(verifyStream, cancellationToken));
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The staged update changed after verification.");
        }

        var isMsix = packagePath.EndsWith(".msix", StringComparison.OrdinalIgnoreCase);
        var isExe = packagePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        if (!isMsix && !isExe) throw new InvalidDataException("A verified MSIX or EXE package is required.");
        if (!HasExpectedPublisher(packagePath, stagedState.ExpectedPublisher))
            throw new InvalidDataException("The staged update failed publisher verification.");

        var rollback = await StageRollbackCopyAsync(currentExecutable, cancellationToken);
        await _stateStore.MarkAsync("installing", packagePath, rollback, cancellationToken);
        try
        {
            if (isMsix)
            {
                var uri = new Uri(packagePath, UriKind.Absolute);
                var manager = new global::Windows.Management.Deployment.PackageManager();
                await manager.AddPackageAsync(uri, null, global::Windows.Management.Deployment.DeploymentOptions.ForceApplicationShutdown);
            }
            else
            {
                // .exe installer: launch silently and exit so the installer can replace files.
                // Inno Setup /SILENT shows a progress bar; /SUPPRESSMSGBOXES prevents dialogs;
                // /NORESTART avoids an automatic reboot. /relaunch=1 tells the installer to
                // reopen the app once files are in place - a silent install skips the
                // Finished-page launch, so without this the app would just close and stay
                // closed after updating.
                // Elevation is requested only for per-machine installs (Program Files);
                // a per-user install lives under %LOCALAPPDATA%\Programs, which the
                // user can already write, so it updates with no UAC prompt at all.
                var psi = new ProcessStartInfo(packagePath, "/SILENT /SUPPRESSMSGBOXES /NORESTART /relaunch=1")
                {
                    UseShellExecute = true,
                    Verb = InstallNeedsElevation(currentExecutable) ? "runas" : "open"
                };
                try
                {
                    using var installer = Process.Start(psi)
                        ?? throw new InvalidOperationException("Could not start the update installer.");
                    await installer.WaitForExitAsync(cancellationToken);
                    if (installer.ExitCode != 0)
                        throw new InvalidOperationException($"The update installer returned exit code {installer.ExitCode}.");
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    // ERROR_CANCELLED: the user declined the UAC prompt. Nothing was
                    // installed, so keep the package staged for a retry and report a
                    // clean cancellation instead of a failure needing rollback.
                    await _stateStore.MarkAsync("staged", packagePath, null, cancellationToken);
                    throw new OperationCanceledException("Administrator permission is required to install the update.");
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    // Windows refused to start the downloaded installer. The dominant
                    // cause is Smart App Control enforcement: SAC blocks any download
                    // without cloud reputation (valid signature or not) and, unlike
                    // SmartScreen, offers no "Run anyway" button. Give the user the
                    // actual reason and an actionable next step instead of the raw
                    // Win32 error text. Nothing was installed, so - like a declined
                    // UAC prompt - keep the package staged (no rollback) and report a
                    // cancellation so the retry path stays available.
                    await _stateStore.MarkAsync("staged", packagePath, null, cancellationToken);
                    var reason = IsSmartAppControlEnforcing
                        ? "Smart App Control is ON in Windows Security and has not built a trust reputation for this download yet (new certificates earn it over time). To update now, download the installer from the releases page, allow it in your security software's protection history if it was blocked, or turn Smart App Control off in Windows Security > App & browser control (turning it off is permanent without resetting Windows)."
                        : "Your antivirus or security software may have quarantined the downloaded installer, or the download is damaged. Check your antivirus protection history, then retry the update or install manually from the releases page.";
                    throw new OperationCanceledException($"Windows refused to start the update installer (error {ex.NativeErrorCode}). {reason}", ex);
                }
            }
            await _stateStore.MarkAsync("installed", packagePath, rollback, cancellationToken);
            CleanupRollbackCopy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _stateStore.MarkAsync("rollback-required", packagePath, rollback, cancellationToken);
            throw;
        }
    }

    /// <summary>True when replacing the running executable needs an administrator,
    /// i.e. the install lives in a directory a standard user cannot write (a
    /// per-machine install under Program Files). A per-user install under
    /// %LOCALAPPDATA%\Programs is writable by its owner, so the silent installer
    /// (PrivilegesRequired=lowest) needs no elevation and no UAC prompt appears.
    /// Probing real writability with a self-deleting file also makes an
    /// already-elevated process report false. Any unexpected probe failure
    /// defaults to true: an unnecessary UAC prompt is better than an installer
    /// that silently cannot write its files.</summary>
    internal static bool InstallNeedsElevation(string currentExecutable)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(currentExecutable));
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return true;
        try
        {
            using (File.Create(Path.Combine(directory, ".update-write-probe"), 1, FileOptions.DeleteOnClose)) { }
            return false;
        }
        catch { return true; }
    }

    public async Task<bool> RollbackAsync(string currentExecutable, CancellationToken cancellationToken = default)
    {
        var state = await _stateStore.LoadAsync(cancellationToken); var rollback = state?.RollbackPath ?? FindRollbackCopy();
        if (string.IsNullOrWhiteSpace(rollback) || !File.Exists(rollback) || !File.Exists(currentExecutable)) return false;
        // Stage the restored copy before touching the live executable so a mid-restore
        // failure never leaves the application without a runnable binary.
        var staged = currentExecutable + ".restore"; var backup = currentExecutable + ".failed";
        try
        {
            File.Copy(rollback, staged, true);
            File.Replace(staged, currentExecutable, backup, ignoreMetadataErrors: true);
            TryDelete(backup);
        }
        catch { TryDelete(staged); throw; }
        await _stateStore.MarkAsync("rolled-back", null, rollback, cancellationToken); return true;
    }

    public Task<string> StageRollbackCopyAsync(string currentExecutable, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); if (!File.Exists(currentExecutable)) throw new FileNotFoundException("Current application was not found.", currentExecutable);
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanMachine", "Updates", "rollback"); Directory.CreateDirectory(directory);
        var copy = Path.Combine(directory, "CleanMachine.previous"); File.Copy(currentExecutable, copy, true); return Task.FromResult(copy);
    }

    public static string? FindRollbackCopy() { var copy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanMachine", "Updates", "rollback", "CleanMachine.previous"); return File.Exists(copy) ? copy : null; }
    public static void CleanupRollbackCopy() { var copy = FindRollbackCopy(); try { if (copy is not null) File.Delete(copy); } catch { } }
    private static UpdatePackage? ResolvePackage(UpdateManifest manifest)
    {
        var arch = CurrentArchitecture();
        // .exe installs use the standalone installer; MSIX installs use the per-architecture package.
        if (!IsInstalledAsMsix)
        {
            if (manifest.Installer is not null && manifest.Installer.Architecture == arch) return manifest.Installer;
            return null; // No installer available for this architecture.
        }
        if (manifest.Packages is not null && manifest.Packages.TryGetValue(arch, out var package)) return package;
        return manifest.Architecture == arch && !string.IsNullOrWhiteSpace(manifest.PackageUrl) ? new UpdatePackage(manifest.PackageUrl, manifest.Sha256, manifest.Architecture, manifest.Publisher) : null;
    }
    private static bool IsValidPackage(UpdatePackage? package)
    {
        // JSON is external input; nullable/missing fields must be rejected rather
        // than reaching string members and turning a malformed manifest into an
        // unhandled exception in the update-check path.
        if (package is null
            || string.IsNullOrWhiteSpace(package.PackageUrl)
            || string.IsNullOrWhiteSpace(package.Sha256)
            || !Uri.TryCreate(package.PackageUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps) return false;
        if (package.Sha256.Length != 64 || !package.Sha256.All(Uri.IsHexDigit)) return false;
        if (package.Architecture != CurrentArchitecture()) return false;
        var isMsix = package.PackageUrl.EndsWith(".msix", StringComparison.OrdinalIgnoreCase);
        var isExe = package.PackageUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        if (!isMsix && !isExe) return false;
        // Both package types must identify the expected release publisher. Hashes
        // provide integrity, while the publisher check prevents a validly hashed
        // but unsigned/re-signed package from entering the install path.
        if (string.IsNullOrWhiteSpace(package.Publisher)) return false;
        return true;
    }
    private static string CurrentArchitecture() => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "ARM64" : RuntimeInformation.OSArchitecture == Architecture.X64 ? "x64" : "x86";
    internal static bool IsNewer(string version) => Version.TryParse(version, out var candidate) && candidate > CurrentVersion();

    /// <summary>The running app's version: the MSIX package identity when packaged,
    /// otherwise the assembly version stamped at build time from the release tag
    /// (the installer build carries no package identity, so Package.Current throws).</summary>
    internal static Version CurrentVersion()
    {
        try { return new(Package.Current.Id.Version.Major, Package.Current.Id.Version.Minor, Package.Current.Id.Version.Build, Package.Current.Id.Version.Revision); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            var assemblyVersion = typeof(UpdateService).Assembly.GetName().Version;
            return assemblyVersion is null ? new(0, 0, 0, 0) : new(assemblyVersion.Major, assemblyVersion.Minor, assemblyVersion.Build, 0);
        }
    }
    private static bool HasExpectedPublisher(string path, string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return false;
        // CreateFromSignedFile extracts the Authenticode signer certificate from a
        // signed package (exe/msix). SYSLIB0057 suggests X509CertificateLoader, but
        // that loads standalone certificate files and cannot read a signed package,
        // so switching would reject every valid MSIX update. The API remains
        // functional in .NET 10; revisit if it is ever removed.
#pragma warning disable SYSLIB0057
        try { using var cert = X509Certificate.CreateFromSignedFile(path); return cert.Subject.Contains(publisher, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
#pragma warning restore SYSLIB0057
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}
