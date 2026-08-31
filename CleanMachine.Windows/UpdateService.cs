using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Windows.ApplicationModel;

namespace CleanMachine.Windows;

public sealed record UpdateManifest(string Version, string ReleaseNotes, string PackageUrl, string Sha256, string Architecture);
public sealed record UpdateCheckResult(bool Available, UpdateManifest? Manifest, string? Error);

public sealed class UpdateService
{
    // Replace with the project's HTTPS release manifest before shipping.
    private static readonly Uri ManifestUri = new("https://github.com/your-org/cleanmachine/releases/latest/download/update-manifest.json");
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = await _httpClient.GetStreamAsync(ManifestUri, cancellationToken);
            var manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, cancellationToken: cancellationToken);
            if (manifest is null || !IsValidManifest(manifest)) return new(false, null, "The release manifest was invalid.");
            return new(IsNewer(manifest.Version), manifest, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(false, null, "Update check could not be completed.");
        }
    }

    public async Task<string> DownloadAndVerifyAsync(UpdateManifest manifest, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(manifest.PackageUrl, UriKind.Absolute, out var packageUri) || packageUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Update package URLs must use HTTPS.");
        var directory = Path.Combine(Path.GetTempPath(), "CleanMachine", "Updates");
        Directory.CreateDirectory(directory);
        var packagePath = Path.Combine(directory, $"CleanMachine-{manifest.Version}.msix");
        await using (var source = await _httpClient.GetStreamAsync(packageUri, cancellationToken))
        await using (var target = File.Create(packagePath)) await source.CopyToAsync(target, cancellationToken);
        await using var verifyStream = File.OpenRead(packagePath);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(verifyStream, cancellationToken));
        if (!hash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(packagePath);
            throw new InvalidDataException("The downloaded update failed hash verification.");
        }
        return packagePath;
    }

    public Task LaunchInstallerAsync(string packagePath)
    {
        if (!File.Exists(packagePath)) throw new FileNotFoundException("Update package not found.", packagePath);
        return Task.Run(() => Process.Start(new ProcessStartInfo(packagePath) { UseShellExecute = true }));
    }

    private static bool IsValidManifest(UpdateManifest manifest) => Version.TryParse(manifest.Version, out _) && Uri.TryCreate(manifest.PackageUrl, UriKind.Absolute, out var package) && package.Scheme == Uri.UriSchemeHttps && manifest.Sha256.Length == 64 && manifest.Sha256.All(Uri.IsHexDigit) && !string.IsNullOrWhiteSpace(manifest.Architecture);
    private static bool IsNewer(string version) => Version.TryParse(version, out var candidate) && candidate > CurrentVersion();
    private static Version CurrentVersion() => new(Package.Current.Id.Version.Major, Package.Current.Id.Version.Minor, Package.Current.Id.Version.Build, Package.Current.Id.Version.Revision);
}
