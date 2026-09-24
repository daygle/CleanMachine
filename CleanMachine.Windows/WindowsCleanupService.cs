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

public sealed record WindowsCleanupOptions(bool ConfirmReviewCategories = false, IReadOnlySet<string>? ExcludedPaths = null, bool SecureDelete = false, SecureDeleteOptions? SecureDeleteOptions = null);

public sealed class WindowsCleanupService
{
    private const int MaxFiles = 10_000;
    private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    // Use the OS-resolved temp directory rather than trusting the mutable TEMP
    // environment variable. Cleanup categories must never be redirected to an
    // arbitrary user-selected path through process environment state.
    private static readonly string Temp = Path.GetFullPath(Path.GetTempPath());
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
        new("explorer-recent-docs", "Windows Explorer", "Recent Documents History", "The recent-documents list (per file type)", CleanupRisk.Safe, true, CleanupKind.RegistryValues, Path: @"Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs"),
        new("explorer-map-drive-mru", "Windows Explorer", "Mapped Drive History", "Remembered 'Map network drive' paths", CleanupRisk.Safe, true, CleanupKind.RegistryValues, Path: @"Software\Microsoft\Windows\CurrentVersion\Explorer\Map Network Drive MRU"),
        new("explorer-icon-cache", "Windows Explorer", "Icon Cache", "Cached icons Windows can recreate", CleanupRisk.Safe, true, CleanupKind.Files, Path: Path.Combine(LocalAppData, "Microsoft", "Windows", "Explorer"), Pattern: "iconcache*.db"),
        new("explorer-jump-lists-custom", "Windows Explorer", "Custom Jump Lists", "App-defined recent-item jump lists", CleanupRisk.Safe, true, CleanupKind.Files, Path: Path.Combine(AppData, "Microsoft", "Windows", "Recent", "CustomDestinations"), Pattern: "*"),

        // ---- Windows System ----
        new("system-temp", "Windows System", "Temporary Files", "Old temporary files no longer in use", CleanupRisk.Safe, true, CleanupKind.Files, Path: Temp, Pattern: "*"),
        new("system-crash-dumps", "Windows System", "Memory Dumps", "Crash dump files from failed processes", CleanupRisk.Safe, true, CleanupKind.Files, Path: Path.Combine(LocalAppData, "CrashDumps"), Pattern: "*.dmp"),
        new("system-error-reports", "Windows System", "Windows Error Reporting", "Old application crash reports and diagnostics", CleanupRisk.Safe, true, CleanupKind.Files, Path: Path.Combine(LocalAppData, "Microsoft", "Windows", "WER"), Pattern: "*"),
        new("system-web-cache", "Windows System", "Windows Web Cache", "Cached web content used by Windows apps", CleanupRisk.Safe, true, CleanupKind.Files, Path: Path.Combine(LocalAppData, "Microsoft", "Windows", "WebCache"), Pattern: "*"),
        new("system-inet-cache", "Windows System", "Internet Cache", "Temporary internet files cached by Windows (WinINet)", CleanupRisk.Safe, true, CleanupKind.Files, Path: Path.Combine(LocalAppData, "Microsoft", "Windows", "INetCache"), Pattern: "*"),
        new("system-rdp-cache", "Windows System", "Remote Desktop Cache", "Cached bitmaps from Remote Desktop sessions", CleanupRisk.Safe, true, CleanupKind.Files, Path: Path.Combine(LocalAppData, "Microsoft", "Terminal Server Client", "Cache"), Pattern: "*"),
        new("system-store-cache", "Windows System", "Microsoft Store Cache", "Store download/image cache (what WSReset clears)", CleanupRisk.Safe, true, CleanupKind.Files, Path: Path.Combine(LocalAppData, "Packages", "Microsoft.WindowsStore_8wekyb3d8bbwe", "LocalCache"), Pattern: "*"),
        new("system-cryptnet-cache", "Windows System", "Certificate Revocation Cache", "Cached certificate revocation lists (CryptnetUrlCache), rebuilt on demand", CleanupRisk.Safe, true, CleanupKind.Files, Path: Path.Combine(LocalAppData, "Microsoft", "CryptnetUrlCache"), Pattern: "*"),
        new("system-spotlight-cache", "Windows System", "Windows Spotlight Cache", "Cached lock-screen/Spotlight images Windows re-downloads", CleanupRisk.Safe, true, CleanupKind.Files, Path: Path.Combine(LocalAppData, "Packages", "Microsoft.Windows.ContentDeliveryManager_cw5n1h2txyewy", "LocalState", "Assets"), Pattern: "*"),
        new("system-powershell-history", "Windows System", "PowerShell Command History", "Saved history of commands typed in PowerShell", CleanupRisk.Safe, true, CleanupKind.Files, Path: Path.Combine(AppData, "Microsoft", "Windows", "PowerShell", "PSReadLine"), Pattern: "ConsoleHost_history.txt"),
        new("system-gpu-nvidia-dx", "Windows System", "NVIDIA DirectX Shader Cache", "Compiled DirectX shaders NVIDIA drivers recreate", CleanupRisk.Safe, true, CleanupKind.Files, Path: Path.Combine(LocalAppData, "NVIDIA", "DXCache"), Pattern: "*"),
        new("system-gpu-nvidia-gl", "Windows System", "NVIDIA OpenGL Shader Cache", "Compiled OpenGL shaders NVIDIA drivers recreate", CleanupRisk.Safe, true, CleanupKind.Files, Path: Path.Combine(LocalAppData, "NVIDIA", "GLCache"), Pattern: "*"),
        new("system-gpu-amd", "Windows System", "AMD Shader Cache", "Compiled shaders AMD drivers recreate", CleanupRisk.Safe, true, CleanupKind.Files, Path: Path.Combine(LocalAppData, "AMD", "DxCache"), Pattern: "*"),
        new("system-gpu-intel", "Windows System", "Intel Shader Cache", "Compiled shaders Intel drivers recreate", CleanupRisk.Safe, true, CleanupKind.Files, Path: Path.Combine(LocalAppData, "Intel", "ShaderCache"), Pattern: "*"),
        new("system-dns-cache", "Windows System", "DNS Cache", "Cached DNS resolver entries", CleanupRisk.Safe, true, CleanupKind.DnsCache),
        new("system-notification-cache", "Windows System", "Notification History", "Action Center notification database (clears past notifications)", CleanupRisk.Review, false, CleanupKind.Files, Path: Path.Combine(LocalAppData, "Microsoft", "Windows", "Notifications"), Pattern: "wpndatabase*"),
        new("system-recycle-bin", "Windows System", "Recycle Bin", "Deleted items awaiting permanent removal", CleanupRisk.Review, false, CleanupKind.RecycleBin),

        // ---- Windows Advanced Options ----
        new("advanced-shader-cache", "Windows Advanced Options", "DirectX Shader Cache", "Compiled shaders for games and apps", CleanupRisk.Safe, true, CleanupKind.Files, Path: Path.Combine(LocalAppData, "D3DSCache"), Pattern: "*"),
        new("advanced-user-assist", "Windows Advanced Options", "User Assist History", "Tracked program-launch history", CleanupRisk.Advanced, false, CleanupKind.RegistryValues, Path: @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist"),
        new("advanced-prefetch", "Windows Advanced Options", "Windows Prefetch Files", "Prefetch data that can slow first launches", CleanupRisk.Advanced, false, CleanupKind.Files, Path: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch"), Pattern: "*"),
        new("advanced-event-logs", "Windows Advanced Options", "Windows Event Logs", "Event log archives", CleanupRisk.Advanced, false, CleanupKind.Files, Path: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "winevt", "Logs"), Pattern: "*.evtx"),
        new("advanced-setupapi-logs", "Windows Advanced Options", "Driver Installation Log Files", "Driver install/update logs", CleanupRisk.Advanced, false, CleanupKind.Files, Path: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF"), Pattern: "setupapi*.log"),
        new("advanced-delivery-optimization", "Windows Advanced Options", "Delivery Optimization Files", "Cached update/install packages", CleanupRisk.Advanced, false, CleanupKind.Files, Path: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "DeliveryOptimization"), Pattern: "*"),
        new("advanced-windows-update-cache", "Windows Advanced Options", "Windows Update Cache", "Downloaded update installers (clearing mid-update can interrupt one)", CleanupRisk.Advanced, false, CleanupKind.Files, Path: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "Download"), Pattern: "*"),

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
                CleanupKind.RecycleBin => GetRecycleBinSize(),
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
        await CleanupCoordinator.Gate.WaitAsync(cancellationToken);
        try
        {
            // Deleting thousands of files (and registry values) is long-running disk
            // work; the UI pages await this directly, so run the whole pass on a
            // worker thread and only the progress callbacks hop back to the UI.
            return await Task.Run(() => CleanSelectedCoreAsync(categories, options, progress, cancellationToken), cancellationToken);
        }
        finally
        {
            CleanupCoordinator.Gate.Release();
        }
    }

    private async Task<CleanupReport> CleanSelectedCoreAsync(IEnumerable<CleanupCategory> categories, WindowsCleanupOptions options, IProgress<CleanupProgress>? progress, CancellationToken cancellationToken)
    {
        var requested = categories.ToArray();
        var catalogById = Catalog.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        var selected = requested
            .Where(category => catalogById.ContainsKey(category.Id))
            .Select(category => catalogById[category.Id])
            .DistinctBy(category => category.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var issues = requested
            .Where(category => !catalogById.ContainsKey(category.Id))
            .Select(category => new CleanupIssue(category.Id, "Unknown cleanup category was rejected by the safety catalog."))
            .ToList();
        var breakdown = new List<CleanupCategoryResult>();
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
            // File discovery can itself take noticeable time in large temp/cache
            // trees. Report the phase before materializing the list so the UI does
            // not appear frozen at the generic "Cleaning..." message.
            progress?.Report(new CleanupProgress($"Preparing {category.Name}", 0, 0, recovered));
            var files = GetCleanableFiles(category, options.ExcludedPaths);
            var categoryRemoved = 0;
            long categoryBytes = 0;
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
                    if (deleted) { removed++; recovered += length; categoryRemoved++; categoryBytes += length; }
                    else issues.Add(new(file, "Protected, locked, or empty - not securely deleted"));
                }
                catch (IOException) { issues.Add(new(file, "Locked or unavailable")); }
                catch (UnauthorizedAccessException) { issues.Add(new(file, "Access denied (administrator may be required)")); }
                progress?.Report(new CleanupProgress(category.Name, index + 1, files.Count, recovered));
            }
            if (categoryRemoved > 0) breakdown.Add(new CleanupCategoryResult(category.Name, categoryRemoved, categoryBytes));
        }

        foreach (var category in selected.Where(c => c.Kind == CleanupKind.RegistryValues))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(category.Path!, writable: true);
                if (key is null) { issues.Add(new(category.Path!, "Registry key not found")); continue; }
                // Recurse sub-keys: many history MRUs (ComDlg32, RecentDocs) store
                // their entries as values inside sub-keys, so clearing only the top
                // level would leave the actual history behind. Sub-keys are kept;
                // only their values are removed.
                var cleared = DeleteValuesRecursive(key, cancellationToken);
                if (cleared == 0) { issues.Add(new(category.Path!, "Nothing to clean")); continue; }
                removed += cleared;
                breakdown.Add(new CleanupCategoryResult(category.Name, cleared, 0));
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
                if (category.Kind == CleanupKind.RecycleBin)
                {
                    // Measure before emptying: the report, Overview stats, and
                    // activity entry should credit the bytes actually freed
                    // instead of always showing 0 B recovered.
                    var binBytes = GetRecycleBinSize();
                    EmptyRecycleBin();
                    removed++;
                    recovered += binBytes;
                    breakdown.Add(new CleanupCategoryResult(category.Name, 1, binBytes));
                }
                else
                {
                    FlushDnsCache();
                    removed++;
                    breakdown.Add(new CleanupCategoryResult(category.Name, 1, 0));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
            {
                issues.Add(new(category.Name, $"Cleanup failed: {ex.Message}"));
            }
        }

        return new CleanupReport(new CleanupResult(removed, recovered), issues, breakdown);
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
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key is null ? 0 : CountValuesRecursive(key);
        }
        catch { return 0; }
    }

    /// <summary>Counts values in a key and all of its sub-keys, matching what the
    /// recursive cleaner will remove.</summary>
    private static long CountValuesRecursive(RegistryKey key)
    {
        long count = key.ValueCount;
        foreach (var subName in key.GetSubKeyNames())
        {
            try { using var child = key.OpenSubKey(subName); if (child is not null) count += CountValuesRecursive(child); }
            catch { /* skip inaccessible sub-keys */ }
        }
        return count;
    }

    /// <summary>Deletes every value in a key and, recursively, in its sub-keys,
    /// leaving the key structure intact. Returns how many values were removed.</summary>
    private static int DeleteValuesRecursive(RegistryKey key, CancellationToken cancellationToken)
    {
        var removed = 0;
        foreach (var name in key.GetValueNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            key.DeleteValue(name, throwOnMissingValue: false);
            removed++;
        }
        foreach (var subName in key.GetSubKeyNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var child = key.OpenSubKey(subName, writable: true);
                if (child is not null) removed += DeleteValuesRecursive(child, cancellationToken);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                /* skip sub-keys we cannot open for writing */
            }
        }
        return removed;
    }

    private static long GetLength(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }

    /// <summary>Gets the total size currently held in all Recycle Bins. The shell API
    /// understands the per-drive $Recycle.Bin layout and avoids treating the hidden
    /// metadata files as ordinary cleanup files.</summary>
    internal static long GetRecycleBinSize()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var info = new SHQueryRecycleBinInfo { Size = (uint)Marshal.SizeOf<SHQueryRecycleBinInfo>() };
        var result = SHQueryRecycleBin(null, ref info);
        return result == 0 && info.ItemCount > 0 && info.TotalSize > 0
            ? info.TotalSize
            : 0;
    }

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
        // Drain the redirected streams: an undrained pipe fills after ~64 KB and the
        // child would then block forever, hanging WaitForExit with it.
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException($"ipconfig /flushdns returned exit code {process.ExitCode}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQueryRecycleBinInfo
    {
        public uint Size;
        public long TotalSize;
        public long ItemCount;
    }

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref SHQueryRecycleBinInfo info);

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);
}
