using Microsoft.Win32;

namespace CleanMachine.Windows;

public sealed class AppCleanupService
{
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string RoamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly string ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string WindowsTemp = Path.GetTempPath();

    /// <summary>Detects all catalog apps and scans their temp files.</summary>
    public Task<IReadOnlyList<AppScan>> ScanAllAsync(CancellationToken token = default)
        => Task.Run<IReadOnlyList<AppScan>>(() => AppCatalog.Definitions.Select(ScanApp).ToArray(), token);

    /// <summary>Scans a single app by its catalog id.</summary>
    public Task<AppScan?> ScanAsync(string appId, CancellationToken token = default)
        => Task.Run(() =>
        {
            var def = AppCatalog.Find(appId);
            return def is null ? null : ScanApp(def);
        }, token);

    /// <summary>Cleans the selected app temp items. Returns files removed and bytes recovered.</summary>
    public async Task<CleanupReport> CleanAsync(
        IEnumerable<(string AppId, int ItemIndex)> selection,
        CancellationToken token = default,
        IProgress<CleanupProgress>? progress = null)
    {
        using var cleaning = CleaningActivity.Begin();
        await CleanupCoordinator.Gate.WaitAsync(token);
        try
        {
        var removed = 0;
        long bytes = 0;
        var skipped = new List<CleanupIssue>();
        var cleanedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Group by app to avoid rescanning.
        var byApp = selection
            .GroupBy(s => s.AppId)
            .ToDictionary(g => g.Key, g => g.Select(s => s.ItemIndex).ToHashSet());

        // Deleting every temp file is long-running disk work; keep it off the
        // caller's (UI) thread. The page awaits this directly, so the deletion
        // loop must not run synchronously.
        await Task.Run(() =>
        {
        foreach (var (appId, indices) in byApp)
        {
            token.ThrowIfCancellationRequested();
            var def = AppCatalog.Find(appId);
            if (def is null) continue;

            var scan = ScanApp(def);
            var totalFiles = indices
                .Where(index => index >= 0 && index < scan.Items.Count)
                .Sum(index => scan.Items[index].FileCount);
            var completedFiles = 0;
            for (var i = 0; i < scan.Items.Count; i++)
            {
                if (!indices.Contains(i)) continue;
                token.ThrowIfCancellationRequested();
                var item = scan.Items[i];
                var isDirectory = Directory.Exists(item.FullPath);
                try
                {
                    if (isDirectory)
                    {
                        foreach (var file in FileEnumeration.Files(item.FullPath))
                        {
                            try
                            {
                                var info = new FileInfo(file);
                                if (info.IsReadOnly) { skipped.Add(new(file, "Read-only")); continue; }
                                var len = info.Length;
                                File.Delete(file);
                                removed++;
                                bytes += len;
                                cleanedPaths.Add(file);
                            }
                            catch (IOException) { skipped.Add(new(file, "Locked")); }
                            catch (UnauthorizedAccessException) { skipped.Add(new(file, "Access denied")); }
                            finally
                            {
                                completedFiles++;
                                progress?.Report(new CleanupProgress($"{def.Name}: removing temporary files", completedFiles, totalFiles, bytes));
                            }
                        }
                        // Remove empty subdirectories bottom-up.
                        RemoveEmptyDirs(item.FullPath);
                    }
                    else if (File.Exists(item.FullPath))
                    {
                        var info = new FileInfo(item.FullPath);
                        if (info.IsReadOnly) { skipped.Add(new(item.FullPath, "Read-only")); continue; }
                        var len = info.Length;
                        File.Delete(item.FullPath);
                        removed++;
                        bytes += len;
                        cleanedPaths.Add(item.FullPath);
                    }
                }
                catch (IOException) { skipped.Add(new(item.FullPath, "Locked")); }
                catch (UnauthorizedAccessException) { skipped.Add(new(item.FullPath, "Access denied")); }
                finally
                {
                    if (!isDirectory)
                    {
                        completedFiles++;
                        progress?.Report(new CleanupProgress($"{def.Name}: removing temporary files", completedFiles, totalFiles, bytes));
                    }
                }
            }
        }
        });

        return new CleanupReport(new CleanupResult(removed, bytes), skipped, CleanedPaths: cleanedPaths);
        }
        finally
        {
            CleanupCoordinator.Gate.Release();
        }
    }

    private static AppScan ScanApp(AppDefinition def)
    {
        var installed = IsInstalled(def);
        var items = new List<AppTempItem>();

        if (!installed) return new AppScan(def.Id, def.Name, def.Group, def.IsStoreApp, false, items);

        foreach (var (root, entries) in def.TempLocations)
        {
            var rootPath = ResolveRoot(root);
            if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath)) continue;

            foreach (var entry in entries)
            {
                // A relative path may contain wildcard segments (e.g. a browser's random
                // per-profile directory, "Profiles\*\cache2"), so expand it to every
                // concrete file/directory that matches before sizing.
                foreach (var fullPath in ExpandPaths(rootPath, entry.RelativePath))
                {
                    try
                    {
                        if (Directory.Exists(fullPath))
                        {
                            var files = FileEnumeration.Files(fullPath).ToArray();
                            var totalBytes = files.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0; } });
                            if (files.Length > 0)
                                items.Add(new AppTempItem(entry.Description, totalBytes, files.Length, fullPath));
                        }
                        else if (File.Exists(fullPath))
                        {
                            var info = new FileInfo(fullPath);
                            if (info.Length > 0)
                                items.Add(new AppTempItem(entry.Description, info.Length, 1, fullPath));
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        return new AppScan(def.Id, def.Name, def.Group, def.IsStoreApp, true, items);
    }

    /// <summary>Expands a relative path that may contain wildcard segments into the set
    /// of existing files/directories under <paramref name="root"/>. Literal segments are
    /// matched exactly; a wildcard segment ("*"/"?") matches sub-directories for
    /// intermediate segments, and any file-or-directory entry for the final segment (so
    /// both "Profiles\*\cache2" and "logs\*.log" resolve). Returned paths are always
    /// concrete - never wildcards - so the cleaner's re-scan matches items by the same
    /// FullPaths it would delete.</summary>
    private static IEnumerable<string> ExpandPaths(string root, string relativePath)
    {
        var segments = relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> current = [root];

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var isLast = i == segments.Length - 1;
            var hasWildcard = segment.Contains('*') || segment.Contains('?');
            var next = new List<string>();

            foreach (var dir in current)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    if (hasWildcard)
                    {
                        next.AddRange(isLast
                            ? Directory.EnumerateFileSystemEntries(dir, segment)
                            : Directory.EnumerateDirectories(dir, segment));
                    }
                    else
                    {
                        var combined = Path.Combine(dir, segment);
                        if (isLast)
                        {
                            if (Directory.Exists(combined) || File.Exists(combined)) next.Add(combined);
                        }
                        else if (Directory.Exists(combined))
                        {
                            next.Add(combined);
                        }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            current = next;
            if (next.Count == 0) break;
        }

        return current;
    }

    private static bool IsInstalled(AppDefinition def)
    {
        // For Store apps, check if the package folder exists.
        if (def.IsStoreApp)
        {
            foreach (var (root, entries) in def.TempLocations)
            {
                if (root != AppDataRoot.LocalAppData) continue;
                var rootPath = ResolveRoot(root);
                if (string.IsNullOrEmpty(rootPath)) continue;
                foreach (var entry in entries)
                {
                    var fullPath = Path.Combine(rootPath, entry.RelativePath);
                    // Check the package root (segments before AC/TempState). Guard the
                    // empty case: a path whose very first segment matched TakeWhile's
                    // stop condition would make Aggregate throw on no elements.
                    var segments = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .TakeWhile(s => !s.Contains("AC") && !s.Contains("TempState"))
                        .ToArray();
                    if (segments.Length == 0) continue;
                    var packageRoot = Path.Combine(segments);
                    if (Directory.Exists(packageRoot)) return true;
                }
            }
            return false;
        }

        // For desktop apps, check common install locations.
        return def.Id switch
        {
            "7zip" => DirExists(ProgramFiles, "7-Zip") || DirExists(ProgramFilesX86, "7-Zip"),
            "github-desktop" => DirExists(LocalAppData, "GitHub Desktop"),
            "notepadpp" => DirExists(ProgramFilesX86, "Notepad++") || DirExists(ProgramFiles, "Notepad++"),
            "nodejs" => DirExists(ProgramFiles, "nodejs") || DirExists(ProgramFilesX86, "nodejs"),
            "steam" => DirExists(ProgramFilesX86, "Steam"),
            "vlc" => DirExists(ProgramFiles, "VideoLAN") || DirExists(ProgramFilesX86, "VideoLAN"),
            "zoom" => DirExists(LocalAppData, "Zoom"),
            "microsoft-office" => DirExists(ProgramFiles, "Microsoft Office") || DirExists(ProgramFilesX86, "Microsoft Office"),
            "onedrive" => DirExists(LocalAppData, "OneDrive"),
            "microsoft-edge" => DirExists(LocalAppData, "Microsoft\\Edge"),
            "discord" => DirExists(RoamingAppData, "discord") || DirExists(LocalAppData, "Discord"),
            "slack" => DirExists(RoamingAppData, "Slack"),
            "spotify" => DirExists(LocalAppData, "Spotify"),
            "teams-classic" => DirExists(RoamingAppData, "Microsoft\\Teams"),
            "vscode" => DirExists(RoamingAppData, "Code") || DirExists(LocalAppData, "Programs\\Microsoft VS Code"),
            "chrome" => DirExists(LocalAppData, "Google\\Chrome"),
            "brave" => DirExists(LocalAppData, "BraveSoftware\\Brave-Browser"),
            "adobe-acrobat" => DirExists(LocalAppData, "Adobe\\Acrobat"),
            "adobe-media-cache" => DirExists(RoamingAppData, "Adobe\\Common"),
            "signal" => DirExists(RoamingAppData, "Signal") || DirExists(LocalAppData, "Programs\\signal-desktop"),
            "postman" => DirExists(RoamingAppData, "Postman") || DirExists(LocalAppData, "Postman"),
            "vivaldi" => DirExists(LocalAppData, "Vivaldi"),
            "opera" => DirExists(RoamingAppData, "Opera Software") || DirExists(LocalAppData, "Opera Software"),
            "epic-games" => DirExists(ProgramFilesX86, "Epic Games") || DirExists(LocalAppData, "EpicGamesLauncher"),
            "thunderbird" => DirExists(RoamingAppData, "Thunderbird") || DirExists(LocalAppData, "Thunderbird"),
            "jetbrains" => DirExists(LocalAppData, "JetBrains") || DirExists(RoamingAppData, "JetBrains"),
            "activity-history" => DirExists(LocalAppData, "ConnectedDevicesPlatform"),
            "defender" => FileExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"), "MpCmdRun.log"),
            "mediaplayer" => DirExists(RoamingAppData, "Microsoft\\Media Player"),
            "autoplay" => DirExists(LocalAppData, "Microsoft\\Windows\\Autoplay"),
            "search" => DirExists(LocalAppData, "Microsoft\\Search"),
            _ => false
        };
    }

    private static string ResolveRoot(AppDataRoot root) => root switch
    {
        AppDataRoot.LocalAppData => LocalAppData,
        AppDataRoot.RoamingAppData => RoamingAppData,
        AppDataRoot.ProgramFiles => ProgramFiles,
        AppDataRoot.ProgramFilesX86 => ProgramFilesX86,
        AppDataRoot.UserProfile => UserProfile,
        AppDataRoot.WindowsTemp => WindowsTemp,
        _ => ""
    };

    private static bool DirExists(string root, string relative)
        => Directory.Exists(Path.Combine(root, relative));

    private static bool FileExists(string dir, string fileName)
        => File.Exists(Path.Combine(dir, fileName));

    private static void RemoveEmptyDirs(string root)
    {
        try
        {
            foreach (var dir in FileEnumeration.Directories(root).OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
