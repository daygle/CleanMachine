using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Windows.ApplicationModel;

namespace CleanMachine.Windows;

public sealed record UpdatePackage(string PackageUrl, string Sha256, string Architecture, string? Publisher = null);
public sealed record UpdateManifest(string Version, string ReleaseNotes, string PackageUrl = "", string Sha256 = "", string Architecture = "", string? Publisher = null, Dictionary<string, UpdatePackage>? Packages = null);
public sealed record UpdateCheckResult(bool Available, UpdateManifest? Manifest, UpdatePackage? Package, string? Error);

public sealed class UpdateService
{
    private static readonly Uri ManifestUri = new("https://github.com/daygle/CleanMachine/releases/latest/download/update-manifest.json");
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly UpdateStateStore _stateStore = new();

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = await _httpClient.GetStreamAsync(ManifestUri, cancellationToken);
            var manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, cancellationToken: cancellationToken);
            if (manifest is null || !Version.TryParse(manifest.Version, out _) || string.IsNullOrWhiteSpace(manifest.ReleaseNotes)) return new(false, null, null, "The release manifest was invalid.");
            var package = ResolvePackage(manifest);
            if (package is null || !IsValidPackage(package)) return new(false, null, null, "No signed update package is available for this device.");
            return new(IsNewer(manifest.Version), manifest, package, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return new(false, null, null, "Update check could not be completed."); }
    }

    public async Task<string> DownloadAndVerifyAsync(UpdatePackage package, CancellationToken cancellationToken = default)
    {
        if (!IsValidPackage(package) || !package.PackageUrl.EndsWith(".msix", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only signed MSIX update packages are supported.");
        var directory = Path.Combine(Path.GetTempPath(), "CleanMachine", "Updates"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"CleanMachine-{DateTime.UtcNow:yyyyMMddHHmmss}-{package.Architecture}.msix");
        try
        {
            await using (var source = await _httpClient.GetStreamAsync(new Uri(package.PackageUrl), cancellationToken))
            await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true)) await source.CopyToAsync(target, cancellationToken);
            await using var verify = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken));
            if (!hash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase) || !HasExpectedPublisher(path, package.Publisher)) throw new InvalidDataException("The downloaded MSIX failed hash or publisher verification.");
            await _stateStore.MarkAsync("staged", path, null, cancellationToken);
            return path;
        }
        catch { TryDelete(path); throw; }
    }

    public async Task InstallVerifiedPackageAsync(string packagePath, string currentExecutable, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(packagePath) || !packagePath.EndsWith(".msix", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A verified MSIX package is required.");
        var rollback = await StageRollbackCopyAsync(currentExecutable, cancellationToken);
        await _stateStore.MarkAsync("installing", packagePath, rollback, cancellationToken);
        try
        {
            var uri = new Uri(packagePath, UriKind.Absolute);
            var manager = new Windows.Management.Deployment.PackageManager();
            var current = Package.Current.Id.FullName;
            await manager.AddPackageAsync(uri, null, Windows.Management.Deployment.DeploymentOptions.ForceApplicationShutdown);
            await _stateStore.MarkAsync("installed", packagePath, rollback, cancellationToken);
            CleanupRollbackCopy();
        }
        catch
        {
            await _stateStore.MarkAsync("rollback-required", packagePath, rollback, cancellationToken);
            throw;
        }
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
    private static UpdatePackage? ResolvePackage(UpdateManifest manifest) { var arch = CurrentArchitecture(); if (manifest.Packages is not null && manifest.Packages.TryGetValue(arch, out var package)) return package; return manifest.Architecture == arch && !string.IsNullOrWhiteSpace(manifest.PackageUrl) ? new UpdatePackage(manifest.PackageUrl, manifest.Sha256, manifest.Architecture, manifest.Publisher) : null; }
    private static bool IsValidPackage(UpdatePackage package) => Uri.TryCreate(package.PackageUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && package.PackageUrl.EndsWith(".msix", StringComparison.OrdinalIgnoreCase) && package.Sha256.Length == 64 && package.Sha256.All(Uri.IsHexDigit) && package.Architecture == CurrentArchitecture() && !string.IsNullOrWhiteSpace(package.Publisher);
    private static string CurrentArchitecture() => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "ARM64" : RuntimeInformation.OSArchitecture == Architecture.X64 ? "x64" : "x86";
    private static bool IsNewer(string version) => Version.TryParse(version, out var candidate) && candidate > CurrentVersion();
    private static Version CurrentVersion() { try { return new(Package.Current.Id.Version.Major, Package.Current.Id.Version.Minor, Package.Current.Id.Version.Build, Package.Current.Id.Version.Revision); } catch { return new(1, 0, 0, 0); } }
    private static bool HasExpectedPublisher(string path, string? publisher) { if (string.IsNullOrWhiteSpace(publisher)) return false; try { using var cert = X509Certificate.CreateFromSignedFile(path); return cert.Subject.Contains(publisher, StringComparison.OrdinalIgnoreCase); } catch { return false; } }
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}
