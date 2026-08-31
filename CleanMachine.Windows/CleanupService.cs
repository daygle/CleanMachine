using Microsoft.Win32;

namespace CleanMachine.Windows;

public sealed record CleanupResult(int ItemsRemoved, long BytesRecovered);
public sealed record RegistryFinding(string Hive, string Path, string Reason, bool LowRisk, int Confidence = 50);
public sealed record RegistryBackup(string FilePath, DateTimeOffset CreatedAt);
public sealed record BrowserCleanupTarget(string Browser, string Category, string Path, long Bytes, bool Selected);

public sealed class CleanupService
{
    private static readonly string[] SafeCacheDirectories = ["Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage"];
    private static readonly string[] SupportedBrowsers = ["chrome", "edge", "firefox"];
    public Task<CleanupResult> CleanSelectedBrowsersAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CleanupResult(0, 0));
    public Task<IReadOnlyList<BrowserCleanupTarget>> ScanBrowsersAsync(IEnumerable<string>? browsers = null, CancellationToken cancellationToken = default)
    {
        var targets = new List<BrowserCleanupTarget>();
        foreach (var browser in (browsers ?? SupportedBrowsers).Intersect(SupportedBrowsers, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var profile in GetProfiles(browser)) foreach (var category in SafeCacheDirectories)
            {
                var path = Path.Combine(profile, category); if (Directory.Exists(path)) targets.Add(new BrowserCleanupTarget(browser, category, path, GetDirectorySize(path), true));
            }
        }
        return Task.FromResult<IReadOnlyList<BrowserCleanupTarget>>(targets);
    }
    public async Task<CleanupReport> CleanBrowserTargetsAsync(IEnumerable<BrowserCleanupTarget> targets, IProgress<CleanupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var allowed = targets.Where(t => t.Selected && SupportedBrowsers.Contains(t.Browser, StringComparer.OrdinalIgnoreCase) && SafeCacheDirectories.Contains(t.Category, StringComparer.OrdinalIgnoreCase)).ToArray(); var files = allowed.SelectMany(t => EnumerateFiles(t.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); var removed = 0; long recovered = 0; var skipped = new List<CleanupIssue>();
        for (var index = 0; index < files.Length; index++) { cancellationToken.ThrowIfCancellationRequested(); var path = files[index]; try { var info = new FileInfo(path); if (info.LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(-10)) { skipped.Add(new(path, "Recently modified")); continue; } File.Delete(path); removed++; recovered += info.Length; } catch (IOException) { skipped.Add(new(path, "File is locked or unavailable")); } catch (UnauthorizedAccessException) { skipped.Add(new(path, "Access denied")); } progress?.Report(new CleanupProgress("Browser cache cleanup", index + 1, files.Length, recovered)); }
        return new CleanupReport(new CleanupResult(removed, recovered), skipped);
    }
    public Task<RegistryBackup> CreateRegistryBackupAsync(IEnumerable<RegistryFinding> findings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanMachine", "Backups"); Directory.CreateDirectory(directory); var backupPath = Path.Combine(directory, $"registry-review-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.txt"); File.WriteAllLines(backupPath, findings.Select(f => $"[{f.Hive}\\{f.Path}] {f.Reason}")); return Task.FromResult(new RegistryBackup(backupPath, DateTimeOffset.UtcNow));
    }
    public Task<IReadOnlyList<RegistryFinding>> ScanRegistrySafelyAsync(CancellationToken cancellationToken = default) { var findings = new List<RegistryFinding>(); ScanUninstallEntries(RegistryHive.CurrentUser, findings); return Task.FromResult<IReadOnlyList<RegistryFinding>>(findings); }
    private static void ScanUninstallEntries(RegistryHive hive, ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default); using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"); if (uninstall is null) return;
        foreach (var name in uninstall.GetSubKeyNames()) { using var entry = uninstall.OpenSubKey(name); var displayName = entry?.GetValue("DisplayName") as string; var uninstallString = entry?.GetValue("UninstallString") as string; if (!string.IsNullOrWhiteSpace(displayName) && string.IsNullOrWhiteSpace(uninstallString)) findings.Add(new RegistryFinding("HKCU", $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{name}", "Uninstall metadata has no removal command", true, 75)); }
    }
    private static IEnumerable<string> GetProfiles(string browser)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); var root = browser.ToLowerInvariant() switch { "chrome" => Path.Combine(local, "Google", "Chrome", "User Data"), "edge" => Path.Combine(local, "Microsoft", "Edge", "User Data"), "firefox" => Path.Combine(roaming, "Mozilla", "Firefox", "Profiles"), _ => string.Empty };
        try { return Directory.Exists(root) ? Directory.EnumerateDirectories(root).Where(p => browser.Equals("firefox", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).Equals("Default", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)).ToArray() : []; } catch { return []; }
    }
    private static IEnumerable<string> EnumerateFiles(string path) { try { return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToArray() : []; } catch { return []; } }
    private static long GetDirectorySize(string path) => EnumerateFiles(path).Sum(f => new FileInfo(f).Length);
}
