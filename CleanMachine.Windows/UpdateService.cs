using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Windows.ApplicationModel;

namespace CleanMachine.Windows;

public sealed record UpdatePackage(string PackageUrl, string Sha256, string Architecture, string? Publisher = null);
public sealed record UpdateManifest(string Version, string ReleaseNotes, string PackageUrl, string Sha256, string Architecture, string? Publisher = null, Dictionary<string, UpdatePackage>? Packages = null);
public sealed record UpdateCheckResult(bool Available, UpdateManifest? Manifest, UpdatePackage? Package, string? Error);

public sealed class UpdateService
{
    private static readonly Uri ManifestUri = new("https://github.com/daygle/CleanMachine/releases/latest/download/update-manifest.json");
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = await _httpClient.GetStreamAsync(ManifestUri, cancellationToken);
            var manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, cancellationToken: cancellationToken);
            if (manifest is null || !Version.TryParse(manifest.Version, out _) || string.IsNullOrWhiteSpace(manifest.ReleaseNotes)) return new(false, null, null, "The release manifest was invalid.");
            var package = ResolvePackage(manifest); if (package is null || !IsValidPackage(package)) return new(false, null, null, "No valid update package is available for this device.");
            return new(IsNewer(manifest.Version), manifest, package, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return new(false, null, null, "Update check could not be completed."); }
    }
    public async Task<string> DownloadAndVerifyAsync(UpdatePackage package, CancellationToken cancellationToken = default)
    {
        if (!IsValidPackage(package)) throw new InvalidOperationException("The update package metadata is invalid.");
        var directory = Path.Combine(Path.GetTempPath(), "CleanMachine", "Updates"); Directory.CreateDirectory(directory); var path = Path.Combine(directory, $"CleanMachine-{DateTime.UtcNow:yyyyMMddHHmmss}-{package.Architecture}.zip");
        await using (var source = await _httpClient.GetStreamAsync(new Uri(package.PackageUrl), cancellationToken)) await using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true)) await source.CopyToAsync(target, cancellationToken);
        await using var verify = File.OpenRead(path); var hash = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken)); if (!hash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase)) { TryDelete(path); throw new InvalidDataException("The downloaded update failed hash verification."); } return path;
    }
    public Task<string> StageRollbackCopyAsync(string currentExecutable, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); if (!File.Exists(currentExecutable)) throw new FileNotFoundException("Current application was not found.", currentExecutable); var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanMachine", "Updates", "rollback"); Directory.CreateDirectory(directory); var copy = Path.Combine(directory, "CleanMachine.previous"); File.Copy(currentExecutable, copy, true); return Task.FromResult(copy);
    }
    public static string? FindRollbackCopy() { var copy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanMachine", "Updates", "rollback", "CleanMachine.previous"); return File.Exists(copy) ? copy : null; }
    public static void CleanupRollbackCopy() { var copy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanMachine", "Updates", "rollback", "CleanMachine.previous"); try { if (File.Exists(copy)) File.Delete(copy); } catch { } }
    public Task LaunchInstallerAsync(string packagePath, string? expectedPublisher = null)
    {
        if (!File.Exists(packagePath)) throw new FileNotFoundException("Update package not found.", packagePath);
        if (Path.GetExtension(packagePath).Equals(".msix", StringComparison.OrdinalIgnoreCase) && (string.IsNullOrWhiteSpace(expectedPublisher) || !HasExpectedPublisher(packagePath, expectedPublisher))) throw new InvalidDataException("The signed update publisher could not be verified.");
        return Task.Run(() => Process.Start(new ProcessStartInfo(packagePath) { UseShellExecute = true }));
    }
    private static UpdatePackage? ResolvePackage(UpdateManifest manifest) { var arch = CurrentArchitecture(); if (manifest.Packages is not null && manifest.Packages.TryGetValue(arch, out var package)) return package; return manifest.Architecture == arch && !string.IsNullOrWhiteSpace(manifest.PackageUrl) ? new UpdatePackage(manifest.PackageUrl, manifest.Sha256, manifest.Architecture, manifest.Publisher) : null; }
    private static bool IsValidPackage(UpdatePackage p) => Uri.TryCreate(p.PackageUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && p.Sha256.Length == 64 && p.Sha256.All(Uri.IsHexDigit) && p.Architecture == CurrentArchitecture();
    private static string CurrentArchitecture() => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "ARM64" : RuntimeInformation.OSArchitecture == Architecture.X64 ? "x64" : "x86";
    private static bool IsNewer(string version) => Version.TryParse(version, out var candidate) && candidate > CurrentVersion();
    private static Version CurrentVersion() { try { return new(Package.Current.Id.Version.Major, Package.Current.Id.Version.Minor, Package.Current.Id.Version.Build, Package.Current.Id.Version.Revision); } catch { return new(1, 0, 0, 0); } }
    private static bool HasExpectedPublisher(string path, string publisher) { try { using var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path); return cert.Subject.Contains(publisher, StringComparison.OrdinalIgnoreCase); } catch { return false; } }
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}
