using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CleanMachine.Windows;

public enum CleanupRisk { Safe, Review, Advanced }
public enum CleanupKind { Files, RegistryValues, RecycleBin, DnsCache }

public sealed record CleanupCategory(
    string Id,
    string Group,
    string Name,
    string Description,
    CleanupRisk Risk,
    bool EnabledByDefault,
    CleanupKind Kind,
    string? Path = null,
    string? Pattern = null,
    string[]? Extensions = null);

public sealed record CleanupItem(CleanupCategory Category, long Bytes);
public sealed record CleanupFileDetail(string Path, long Bytes);
public sealed record CleanupPreviewItem(string Category, string Description, long Bytes);
public sealed record CleanupPreview(IReadOnlyList<CleanupPreviewItem> Items, int TotalItems);

public sealed record WindowsCleanupOptions(bool ConfirmReviewCategories = false, bool AllowElevation = false, IReadOnlySet<string>? ExcludedPaths = null, bool SecureDelete = false, SecureDeleteOptions? SecureDeleteOptions = null);

public sealed class WindowsCleanupService
{
    private const int MaxFiles = 10_000;

    private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string Temp = Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath();
    private static readonly string Downloads = Path.Combine(UserProfile, "Downloads");

    /// <summary>Well-known, recreatable locations the app is allowed to clean.</summary>
    private static readonly string[] TrustedCleanupRoots =
    [
        LocalAppData, AppData, Temp, Downloads,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "winevt", "Logs"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution")
    ];

    public static IReadOnlyList<CleanupCategory> Catalog { get; } =
    [
        // ---- Windows Explorer ----
        new("explorer-recent", "Windows Explorer", "Recent Items", "Recently opened documents and files", CleanupRisk.Safe, true, CleanupKind.Files, Path: Path.Combine(AppData, "Microsoft", "Windows", "Recent"), Pattern: "*.lnk"),
        new("explorer-run-history", "Windows Explorer", "Start Menu Run History", "Commands typed into the Run dialog", CleanupRisk.Safe, true, CleanupKind.RegistryValues, Path: @"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU"),
        new("explorer-search-history", "Windows Explorer", "Windows Search History", "Searches typed into the Start menu / search box", CleanupRisk.Safe, true, CleanupKind.RegistryValues, Path: @"Software\Microsoft\Windows\CurrentVersion\Explorer\WordWheelQuery"),
        new("explorer-open-save-history", "Windows Explorer", "Open & Save Dialog History", "Recent locations in open/save dialogs", CleanupRisk.Safe, true, CleanupKind.RegistryValues, Path: @"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32"),
        new("explorer-jump-lists", "Windows Explorer", "Taskbar Jump Lists", "Recent-file jump lists for taskbar apps (Quick Access pins are preserved)", CleanupRisk.Safe, true, CleanupKind.Files, Path: Path.Combine(AppData, "Microsoft", "Windows", "Recent", "AutomaticDestinations"), Pattern: "*"),
        new("explorer-thumbnails", "Windows Explorer", "Thumbnail Cache", "Cached image previews Windows can recreate", CleanupRisk.Safe, true, CleanupKind.Files, Path: Path.Combine(LocalAppData, "Microsoft", "Windows", "Explorer"), Pattern: "thumbcache*.db"),
        new("explorer-typed-paths", "Windows Explorer", "Other Explorer MRUs", "Typed paths and other Explorer history", CleanupRisk.Safe, true, CleanupKind.RegistryValues, Path: @"Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths"),

        // ---- Windows System ----
        new("system-temp", "Windows System", "Temporary Files", "Old temporary files no longer in use", CleanupRisk.Safe, true, CleanupKind.Files, Path: Temp, Pattern: "*"),
        new("system-crash-dumps", "Windows System", "Memory Dumps", "Crash dump files from failed processes", CleanupRisk.Safe, true, CleanupKind.Files, Path: Path.Combine(LocalAppData, "CrashDumps"), Pattern: "*.dmp"),
        new("system-error-reports", "Windows System", "Windows Error Reporting", "Old application crash reports and diagnostics", CleanupRisk.Safe, true, CleanupKind.Files, Path: Path.Combine(LocalAppData, "Microsoft", "Windows", "WER"), Pattern: "*"),
        new("system-web-cache", "Windows System", "Windows Web Cache", "Cached web content used by Windows apps", CleanupRisk.Safe, true, CleanupKind.Files, Path: Path.Combine(LocalAppData, "Microsoft", "Windows", "WebCache"), Pattern: "*"),
        new("system-dns-cache", "Windows System", "DNS Cache", "Cached DNS resolver entries", CleanupRisk.Safe, true, CleanupKind.DnsCache),
        new("system-recycle-bin", "Windows System", "Recycle Bin", "Deleted items awaiting permanent removal", CleanupRisk.Review, false, CleanupKind.RecycleBin),

        // ---- Windows Advanced Options ----
        new("advanced-shader-cache", "Windows Advanced Options", "DirectX Shader Cache", "Compiled shaders for games and apps", CleanupRisk.Safe, true, CleanupKind.Files, Path: Path.Combine(LocalAppData, "D3DSCache"), Pattern: "*"),
        new("advanced-user-assist", "Windows Advanced Options", "User Assist History", "Tracked program-launch history", CleanupRisk.Advanced, false, CleanupKind.RegistryValues, Path: @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist"),
        new("advanced-prefetch", "Windows Advanced Options", "Windows Prefetch Files", "Prefetch data that can slow first launches", CleanupRisk.Advanced, false, CleanupKind.Files, Path: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch"), Pattern: "*"),
        new("advanced-event-logs", "Windows Advanced Options", "Windows Event Logs", "Event log archives", CleanupRisk.Advanced, false, CleanupKind.Files, Path: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "winevt", "Logs"), Pattern: "*.evtx"),
        new("advanced-setupapi-logs", "Windows Advanced Options", "Driver Installation Log Files", "Driver install/update logs", CleanupRisk.Advanced, false, CleanupKind.Files, Path: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF"), Pattern: "setupapi*.log"),
        new("advanced-delivery-optimization", "Windows Advanced Options", "Delivery Optimization Files", "Cached update/install packages", CleanupRisk.Advanced, false, CleanupKind.Files, Path: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "DeliveryOptimization"), Pattern: "*"),

        // ---- Windows Downloads (user files: disabled by default, require confirmation) ----
        new("downloads-apps", "Windows Downloads", "Apps/Programs", "Downloaded installers (exe/msi) - user data, confirm before cleaning", CleanupRisk.Review, false, CleanupKind.Files, Path: Downloads, Extensions: [".exe", ".msi", ".msix", ".appx"]),
        new("downloads-archives", "Windows Downloads", "Compressed Files", "Downloaded archives (zip/rar/7z) - user data, confirm before cleaning", CleanupRisk.Review, false, CleanupKind.Files, Path: Downloads, Extensions: [".zip", ".rar", ".7z", ".tar", ".gz", ".iso"]),
        new("downloads-images", "Windows Downloads", "Pictures/Images", "Downloaded images - user data, confirm before cleaning", CleanupRisk.Review, false, CleanupKind.Files, Path: Downloads, Extensions: [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".svg"]),
        new("downloads-media", "Windows Downloads", "Audio/Video", "Downloaded media - user data, confirm before cleaning", CleanupRisk.Review, false, CleanupKind.Files, Path: Downloads, Extensions: [".mp3", ".mp4", ".wav", ".flac", ".mov", ".mkv", ".avi"]),
        new("downloads-docs", "Windows Downloads", "Documents", "Downloaded documents - user data, confirm before cleaning", CleanupRisk.Review, false, CleanupKind.Files, Path: Downloads, Extensions: [".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt"]),
        new("downloads-other", "Windows Downloads", "Others", "Other downloaded files - user data, confirm before cleaning", CleanupRisk.Review, false, CleanupKind.Files, Path: Downloads, Extensions: [".dll", ".bin", ".dat", ".tmp"])
    ];

    public static bool IsEnabled(CleanupCategory category, AppSettings settings)
        => settings.EnabledCleanupCategories.Contains(category.Id)
           || (!settings.DisabledCleanupCategories.Contains(category.Id) && category.EnabledByDefault);

    public IReadOnlyList<CleanupItem> Scan(IReadOnlySet<string>? excludedPaths = null)
    {
        var items = new List<CleanupItem>(Catalog.Count);
        foreach (var category in Catalog)
        {
            long bytes = category.Kind switch
            {
                CleanupKind.Files => GetDirectorySize(category.Path!, category.Pattern, category.Extensions, excludedPaths),
                CleanupKind.RegistryValues => CountRegistryValues(category.Path!),
                _ => 0
            };
            items.Add(new CleanupItem(category, bytes));
        }
        return items;
    }

    /// <summary>Returns the individual cleanable files for a file-based category,
    /// with their sizes. Used by the UI to show a detailed file list when a
    /// category is clicked after analysis.</summary>
    public IReadOnlyList<CleanupFileDetail> ScanFiles(CleanupCategory category, IReadOnlySet<string>? excludedPaths = null)
    {
        if (category.Kind != CleanupKind.Files) return [];
        return GetCleanableFiles(category, excludedPaths)
            .Select(f => new CleanupFileDetail(f, GetLength(f)))
            .ToList();
    }

    public async Task<CleanupReport> CleanSelectedAsync(IEnumerable<CleanupCategory> categories, WindowsCleanupOptions options, IProgress<CleanupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var selected = categories.ToArray();
        var issues = new List<CleanupIssue>();
        var removed = 0;
        long recovered = 0;
        var requiresConfirmation = selected.Any(c => c.Risk != CleanupRisk.Safe);
        if (requiresConfirmation && !options.ConfirmReviewCategories)
        {
            var names = string.Join(", ", selected.Where(c => c.Risk != CleanupRisk.Safe).Select(c => c.Name));
            issues.Add(new CleanupIssue("review-confirmation", $"Explicit review confirmation required for: {names}"));
            return new CleanupReport(new CleanupResult(0, 0), issues);
        }

        foreach (var category in selected.Where(c => c.Kind == CleanupKind.Files))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = GetCleanableFiles(category, options.ExcludedPaths);
            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = files[index];
                try
                {
                    var length = new FileInfo(file).Length;
                    var deleted = true;
                    if (options.SecureDelete && options.SecureDeleteOptions is not null)
                        deleted = await SecureDeleteService.SecureDeleteFileAsync(file, options.SecureDeleteOptions, cancellationToken);
                    else
                        File.Delete(file);
                    if (deleted) { removed++; recovered += length; }
                    else issues.Add(new(file, "Protected, locked, or empty - not securely deleted"));
                }
                catch (IOException) { issues.Add(new(file, "Locked or unavailable")); }
                catch (UnauthorizedAccessException) { issues.Add(new(file, "Access denied (administrator may be required)")); }
                progress?.Report(new CleanupProgress(category.Name, index + 1, files.Count, recovered));
            }
        }

        foreach (var category in selected.Where(c => c.Kind == CleanupKind.RegistryValues))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(category.Path!, writable: true);
                if (key is null) { issues.Add(new(category.Path!, "Registry key not found")); continue; }
                var names = key.GetValueNames();
                if (names.Length == 0) { issues.Add(new(category.Path!, "Nothing to clean")); continue; }
                foreach (var name in names)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    key.DeleteValue(name, throwOnMissingValue: false);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                issues.Add(new(category.Path!, $"Registry cleanup failed: {ex.Message}"));
            }
        }

        foreach (var category in selected.Where(c => c.Kind is CleanupKind.RecycleBin or CleanupKind.DnsCache))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (category.Kind == CleanupKind.RecycleBin) { EmptyRecycleBin(); removed++; }
                else { FlushDnsCache(); removed++; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
            {
                issues.Add(new(category.Name, $"Cleanup failed: {ex.Message}"));
            }
        }

        return new CleanupReport(new CleanupResult(removed, recovered), issues);
    }

    public CleanupPreview BuildPreview(IEnumerable<CleanupCategory> categories, IReadOnlySet<string>? excludedPaths = null, int maxFileItems = 100)
    {
        var shown = new List<CleanupPreviewItem>();
        var total = 0;
        var fileShown = 0;
        foreach (var category in categories)
        {
            switch (category.Kind)
            {
                case CleanupKind.Files:
                    foreach (var file in GetCleanableFiles(category, excludedPaths))
                    {
                        total++;
                        if (fileShown < maxFileItems)
                        {
                            shown.Add(new CleanupPreviewItem(category.Name, file, GetLength(file)));
                            fileShown++;
                        }
                    }
                    break;
                case CleanupKind.RegistryValues:
                    var count = CountRegistryValues(category.Path!);
                    total += (int)count;
                    if (count > 0) shown.Add(new CleanupPreviewItem(category.Name, $"{count:N0} registry value(s) to clear", 0));
                    break;
                case CleanupKind.RecycleBin:
                    total++;
                    shown.Add(new CleanupPreviewItem(category.Name, "Empty the Recycle Bin", 0));
                    break;
                case CleanupKind.DnsCache:
                    total++;
                    shown.Add(new CleanupPreviewItem(category.Name, "Flush the DNS cache", 0));
                    break;
            }
        }
        return new CleanupPreview(shown, total);
    }

    private static IReadOnlyList<string> GetCleanableFiles(CleanupCategory category, IReadOnlySet<string>? excludedPaths)
        => EnumerateCleanableFiles(category.Path!, category.Pattern, category.Extensions, excludedPaths)
            .Where(f => !IsRecentlyModified(f))
            .ToArray();

    private static bool IsRecentlyModified(string path)
    {
        try { return File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddHours(-2); }
        catch { return false; }
    }

    private static IEnumerable<string> EnumerateCleanableFiles(string directory, string? pattern, string[]? extensions, IReadOnlySet<string>? exclusions)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) || !IsTrustedRoot(directory) || IsExcluded(directory, exclusions)) return [];
        try
        {
            return Directory.EnumerateFiles(directory, pattern ?? "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            })
            .Where(f => !IsExcluded(f, exclusions) && !NativeSafety.IsReparsePoint(f) && MatchesExtensions(f, extensions) && !IsProtectedFile(f))
            .Take(MaxFiles)
            .ToArray();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    // The Quick Access pinned-links list is stored as one of the AutomaticDestinations
    // jump-list files. Deleting it wipes the user's pinned Quick Access folders, so it is
    // never cleaned or counted; per-app/taskbar jump lists in the same folder still are.
    private static readonly string[] ProtectedFileNames =
    [
        "f01b4d95cf55d32a.automaticDestinations-ms" // Windows Explorer / Quick Access
    ];

    private static bool IsProtectedFile(string path)
        => ProtectedFileNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    private static bool MatchesExtensions(string path, string[]? extensions)
        => extensions is null || extensions.Length == 0
           || extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool IsTrustedRoot(string directory)
    {
        try { return TrustedCleanupRoots.Any(root => NativeSafety.IsWithin(directory, root)); }
        catch { return false; }
    }

    private static bool IsExcluded(string path, IReadOnlySet<string>? exclusions) => exclusions?.Any(root => NativeSafety.IsWithin(path, root)) == true;

    private static long GetDirectorySize(string directory, string? pattern, string[]? extensions, IReadOnlySet<string>? exclusions)
        => EnumerateCleanableFiles(directory, pattern, extensions, exclusions).Sum(GetLength);

    private static long CountRegistryValues(string keyPath)
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(keyPath); return key?.ValueCount ?? 0; }
        catch { return 0; }
    }

    private static long GetLength(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }

    private static void EmptyRecycleBin()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Recycle Bin cleanup is supported on Windows only.");
        var result = SHEmptyRecycleBin(IntPtr.Zero, null, 0x00000001 | 0x00000002 | 0x00000004);
        if (result != 0) throw new IOException($"Windows returned error code {result}.");
    }

    private static void FlushDnsCache()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DNS cache flush is supported on Windows only.");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("ipconfig", "/flushdns")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.Start();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException($"ipconfig /flushdns returned exit code {process.ExitCode}.");
    }

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);
}
