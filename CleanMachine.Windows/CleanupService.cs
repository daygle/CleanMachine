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

    public Task<CleanupResult> CleanSelectedBrowsersAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new CleanupResult(0, 0));

    public Task<IReadOnlyList<BrowserCleanupTarget>> ScanBrowsersAsync(
        IEnumerable<string>? browsers = null,
        IEnumerable<string>? additionalProfileRoots = null,
        IReadOnlySet<string>? excludedPaths = null,
        CancellationToken cancellationToken = default)
    {
        var targets = new List<BrowserCleanupTarget>();
        foreach (var browser in (browsers ?? SupportedBrowsers).Intersect(SupportedBrowsers, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var profile in GetProfiles(browser, additionalProfileRoots))
            {
                if (IsExcluded(profile, excludedPaths)) continue;
                foreach (var category in SafeCacheDirectories)
                {
                    var path = Path.Combine(profile, category);
                    if (Directory.Exists(path) && !IsExcluded(path, excludedPaths))
                        targets.Add(new BrowserCleanupTarget(browser, category, path, GetDirectorySize(path), true));
                }
            }
        }
        return Task.FromResult<IReadOnlyList<BrowserCleanupTarget>>(targets);
    }

    public async Task<CleanupReport> CleanBrowserTargetsAsync(
        IEnumerable<BrowserCleanupTarget> targets,
        IProgress<CleanupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var allowed = targets
            .Where(t => t.Selected
                && SupportedBrowsers.Contains(t.Browser, StringComparer.OrdinalIgnoreCase)
                && SafeCacheDirectories.Contains(t.Category, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var files = allowed
            .SelectMany(t => EnumerateFiles(t.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var removed = 0;
        long recovered = 0;
        var skipped = new List<CleanupIssue>();

        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = files[index];
            try
            {
                var info = new FileInfo(path);
                if (info.LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(-10))
                {
                    skipped.Add(new(path, "Recently modified"));
                    continue;
                }
                File.Delete(path);
                removed++;
                recovered += info.Length;
            }
            catch (IOException) { skipped.Add(new(path, "File is locked or unavailable")); }
            catch (UnauthorizedAccessException) { skipped.Add(new(path, "Access denied")); }
            progress?.Report(new CleanupProgress("Browser cache cleanup", index + 1, files.Length, recovered));
        }
        return new CleanupReport(new CleanupResult(removed, recovered), skipped);
    }

    public Task<RegistryBackup> CreateRegistryBackupAsync(
        IEnumerable<RegistryFinding> findings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CleanMachine", "Backups");
        Directory.CreateDirectory(directory);
        var backupPath = Path.Combine(directory, $"registry-review-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.txt");
        File.WriteAllLines(backupPath, findings.Select(f => $"[{f.Hive}\\{f.Path}] {f.Reason}"));
        return Task.FromResult(new RegistryBackup(backupPath, DateTimeOffset.UtcNow));
    }

    public Task<IReadOnlyList<RegistryFinding>> ScanRegistrySafelyAsync(
        CancellationToken cancellationToken = default)
    {
        var findings = new List<RegistryFinding>();
        ScanUninstallEntries(RegistryHive.CurrentUser, findings);
        ScanFileAssociations(RegistryHive.CurrentUser, findings);
        return Task.FromResult<IReadOnlyList<RegistryFinding>>(findings);
    }

    private static void ScanUninstallEntries(RegistryHive hive, ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
        if (uninstall is null) return;
        foreach (var name in uninstall.GetSubKeyNames())
        {
            using var entry = uninstall.OpenSubKey(name);
            var displayName = entry?.GetValue("DisplayName") as string;
            var uninstallString = entry?.GetValue("UninstallString") as string;
            if (!string.IsNullOrWhiteSpace(displayName) && string.IsNullOrWhiteSpace(uninstallString))
                findings.Add(new RegistryFinding("HKCU",
                    $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{name}",
                    "Uninstall metadata has no removal command", true, 75));
        }
    }

    private static void ScanFileAssociations(RegistryHive hive, ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using var extensions = root.OpenSubKey(@"Software\Classes\.obsolete");
        if (extensions is null) return;
        foreach (var ext in extensions.GetSubKeyNames())
        {
            using var extKey = extensions.OpenSubKey(ext);
            var progId = extKey?.GetValue("") as string;
            if (string.IsNullOrWhiteSpace(progId)) continue;
            using var progIdKey = root.OpenSubKey($@"Software\Classes\{progId}\shell");
            if (progIdKey is null)
                findings.Add(new RegistryFinding("HKCU", $@"Software\Classes\{ext}",
                    $"File extension maps to missing handler '{progId}'", true, 65));
        }
    }

    private static IEnumerable<string> GetProfiles(
        string browser,
        IEnumerable<string>? additionalRoots = null)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var roots = new List<string>();
        switch (browser.ToLowerInvariant())
        {
            case "chrome":
                roots.Add(Path.Combine(local, "Google", "Chrome", "User Data"));
                roots.Add(Path.Combine(programFiles, "Google", "Chrome", "User Data"));
                roots.Add(Path.Combine(programFilesX86, "Google", "Chrome", "User Data"));
                break;
            case "edge":
                roots.Add(Path.Combine(local, "Microsoft", "Edge", "User Data"));
                roots.Add(Path.Combine(programFiles, "Microsoft", "Edge", "User Data"));
                break;
            case "firefox":
                roots.Add(Path.Combine(roaming, "Mozilla", "Firefox", "Profiles"));
                roots.Add(Path.Combine(local, "Mozilla", "Firefox", "Profiles"));
                break;
        }

        if (additionalRoots is not null)
            roots.AddRange(additionalRoots);

        var profiles = new List<string>();
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var name = Path.GetFileName(dir);
                    if (browser.Equals("firefox", StringComparison.OrdinalIgnoreCase))
                    {
                        // Firefox profiles are hex-named dirs containing a profile ini reference
                        profiles.Add(dir);
                    }
                    else
                    {
                        // Chromium: Default, Profile 1, Profile 2, etc.
                        if (name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
                            profiles.Add(dir);
                    }
                }
            }
            catch { /* inaccessible */ }
        }
        return profiles;
    }

    private static IEnumerable<string> EnumerateFiles(string path)
    {
        try { return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToArray() : []; }
        catch { return []; }
    }

    private static long GetDirectorySize(string path)
        => EnumerateFiles(path).Sum(f => { try { return new FileInfo(f).Length; } catch { return 0; } });

    private static bool IsExcluded(string path, IReadOnlySet<string>? exclusions)
        => exclusions?.Any(root => NativeSafety.IsWithin(path, root)) == true;
}
