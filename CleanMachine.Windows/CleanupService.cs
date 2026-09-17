using Microsoft.Win32;

namespace CleanMachine.Windows;

public sealed record CleanupResult(int ItemsRemoved, long BytesRecovered);
public sealed record RegistryFinding(string Hive, string Path, string Reason, bool LowRisk, int Confidence = 50, string Category = "Other", string? ValueName = null);
public sealed record RegistryBackup(string FilePath, DateTimeOffset CreatedAt);
public sealed record BrowserCleanupTarget(string Browser, string Category, string Path, long Bytes, bool Selected);

public sealed class CleanupService
{
    private static readonly string[] SafeCacheDirectories = ["Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage"];
    private static readonly string[] SupportedBrowsers = ["chrome", "edge", "firefox"];

    public Task<IReadOnlyList<BrowserCleanupTarget>> ScanBrowsersAsync(
        IEnumerable<string>? browsers = null,
        IEnumerable<string>? additionalProfileRoots = null,
        IReadOnlySet<string>? excludedPaths = null,
        CancellationToken cancellationToken = default)
        // Profile and cache-directory sizing is disk-bound work that can take a
        // while; callers await this directly (e.g. from the UI thread), so the
        // walk itself must run on a worker thread.
        => Task.Run<IReadOnlyList<BrowserCleanupTarget>>(() =>
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
        return targets;
        }, cancellationToken);

    public async Task<CleanupReport> CleanBrowserTargetsAsync(
        IEnumerable<BrowserCleanupTarget> targets,
        IProgress<CleanupProgress>? progress = null,
        SecureDeleteOptions? secureDelete = null,
        CancellationToken cancellationToken = default)
        // Deleting (and securely overwriting) every cache file is long-running;
        // keep it off the caller's (UI) thread for the whole pass.
        => await Task.Run(async () =>
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
                var deleted = true;
                if (secureDelete is not null)
                    deleted = await SecureDeleteService.SecureDeleteFileAsync(path, secureDelete, cancellationToken);
                else
                    File.Delete(path);
                if (deleted) { removed++; recovered += info.Length; }
                else skipped.Add(new(path, "Protected, locked, or empty - not securely deleted"));
            }
            catch (IOException) { skipped.Add(new(path, "File is locked or unavailable")); }
            catch (UnauthorizedAccessException) { skipped.Add(new(path, "Access denied")); }
            progress?.Report(new CleanupProgress("Browser cache cleanup", index + 1, files.Length, recovered));
        }
        return new CleanupReport(new CleanupResult(removed, recovered), skipped);
        }, cancellationToken);

    public Task<IReadOnlyList<RegistryFinding>> ScanRegistrySafelyAsync(
        CancellationToken cancellationToken = default)
        // Walking many registry keys is disk-bound; callers await this on the UI
        // thread, so the scan itself must run on a worker thread.
        => Task.Run<IReadOnlyList<RegistryFinding>>(() =>
        {
        var findings = new List<RegistryFinding>();
        ScanUninstallEntries(RegistryHive.CurrentUser, findings);
        ScanFileAssociations(RegistryHive.CurrentUser, findings);
        ScanMuiCache(findings);
        ScanStartupEntries(findings);
        ScanSoundAppEvents(findings);
        ScanShellMuiCache(findings);
        ScanUserAppPaths(findings);
        ScanOpenWithProgids(findings);
        ScanOpenWithList(findings);
        ScanCompatibilityAssistant(findings);
        return findings;
        }, cancellationToken);

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
            if (string.IsNullOrWhiteSpace(displayName)) continue;
            if (string.IsNullOrWhiteSpace(uninstallString))
            {
                findings.Add(new RegistryFinding("HKCU",
                    $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{name}",
                    "Uninstall metadata has no removal command", true, 75, "Installer/Uninstaller"));
                continue;
            }
            // The uninstall command names an uninstaller that is gone: the program
            // is no longer installed, so the leftover entry can't uninstall anything.
            // Only act when we can resolve an absolute local exe (MsiExec and env-var
            // commands resolve to null and are left alone).
            var uninstaller = ResolveStartupExecutable(uninstallString);
            if (uninstaller is not null && !File.Exists(uninstaller))
                findings.Add(new RegistryFinding("HKCU",
                    $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{name}",
                    $"Uninstaller is missing ({Path.GetFileName(uninstaller)})", true, 70, "Installer/Uninstaller"));
        }
    }

    // Per-user file-type associations (HKCU\Software\Classes\.ext) whose default
    // ProgID has no handler class anywhere in the merged HKCR view (HKLM + HKCU) -
    // a dangling association. Removing the per-user key falls back to the system
    // default, so it only ever undoes a broken override.
    private static void ScanFileAssociations(RegistryHive hive, ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using var classes = root.OpenSubKey(@"Software\Classes");
        if (classes is null) return;
        foreach (var ext in classes.GetSubKeyNames())
        {
            if (!ext.StartsWith('.')) continue; // only file-extension keys
            using var extKey = classes.OpenSubKey(ext);
            if (extKey?.GetValue(null) is not string progId || string.IsNullOrWhiteSpace(progId)) continue;
            // Resolve the ProgID against the merged HKCR view. Two orphan cases:
            // the class is entirely absent, or it exists but its open command runs
            // an executable that is gone. Either way the per-user override is broken;
            // removing it falls back to the system default.
            using var handler = Registry.ClassesRoot.OpenSubKey(progId);
            if (handler is null)
                findings.Add(new RegistryFinding("HKCU", $@"Software\Classes\{ext}",
                    $"File type {ext} maps to a missing handler '{progId}'", true, 70, "File Extensions"));
            else if (TryGetMissingOpenCommandExe(progId, out var exe))
                findings.Add(new RegistryFinding("HKCU", $@"Software\Classes\{ext}",
                    $"File type {ext} opens with a missing program ({Path.GetFileName(exe)})", true, 70, "File Extensions"));
        }
    }

    /// <summary>True when a ProgID's shell\open\command resolves to an absolute local
    /// executable that no longer exists. Commands we cannot positively resolve to a
    /// fully-qualified path (MsiExec, rundll32, env-var or store activations) return
    /// false so they are never flagged.</summary>
    private static bool TryGetMissingOpenCommandExe(string progId, out string? exe)
    {
        exe = null;
        using var command = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
        if (command?.GetValue(null) is not string raw || string.IsNullOrWhiteSpace(raw)) return false;
        var candidate = ResolveStartupExecutable(raw);
        if (candidate is null || File.Exists(candidate)) return false;
        exe = candidate;
        return true;
    }

    // Per-extension "Open with" ProgID suggestions
    // (HKCU\...\Explorer\FileExts\.ext\OpenWithProgids) that reference a class no
    // longer registered anywhere. Each is a single value; deleting it only removes
    // a dead entry from the "Open with" list.
    private const string FileExtsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";

    private static void ScanOpenWithProgids(ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var fileExts = root.OpenSubKey(FileExtsKey);
        if (fileExts is null) return;
        foreach (var ext in fileExts.GetSubKeyNames())
        {
            using var progids = fileExts.OpenSubKey($@"{ext}\OpenWithProgids");
            if (progids is null) continue;
            foreach (var progId in progids.GetValueNames())
            {
                if (string.IsNullOrEmpty(progId)) continue;
                using var handler = Registry.ClassesRoot.OpenSubKey(progId);
                if (handler is null)
                    findings.Add(new RegistryFinding("HKCU", $@"{FileExtsKey}\{ext}\OpenWithProgids",
                        $"'Open with' entry for {ext} references a missing handler '{progId}'", true, 70, "Open With", progId));
                else if (TryGetMissingOpenCommandExe(progId, out var exe))
                    findings.Add(new RegistryFinding("HKCU", $@"{FileExtsKey}\{ext}\OpenWithProgids",
                        $"'Open with' entry for {ext} runs a missing program ({Path.GetFileName(exe)})", true, 70, "Open With", progId));
            }
        }
    }

    // Per-extension "Open with" application list
    // (HKCU\...\Explorer\FileExts\.ext\OpenWithList) whose entry is an absolute path
    // to a program that no longer exists. Bare executable names (resolved via App
    // Paths / PATH) can't be positively verified, so only full paths are flagged.
    private static void ScanOpenWithList(ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var fileExts = root.OpenSubKey(FileExtsKey);
        if (fileExts is null) return;
        foreach (var ext in fileExts.GetSubKeyNames())
        {
            using var list = fileExts.OpenSubKey($@"{ext}\OpenWithList");
            if (list is null) continue;
            foreach (var valueName in list.GetValueNames())
            {
                // MRUList just orders the letters; the a/b/c values hold the programs.
                if (string.IsNullOrEmpty(valueName) || valueName.Equals("MRUList", StringComparison.OrdinalIgnoreCase)) continue;
                if (list.GetValue(valueName) is not string target || string.IsNullOrWhiteSpace(target)) continue;
                var exe = target.Trim('"');
                if (exe.Contains('%') || !Path.IsPathFullyQualified(exe)) continue;
                if (File.Exists(exe)) continue;
                findings.Add(new RegistryFinding("HKCU", $@"{FileExtsKey}\{ext}\OpenWithList",
                    $"'Open with' entry for {ext} points to a missing program ({Path.GetFileName(exe)})", true, 70, "Open With", valueName));
            }
        }
    }

    // Program Compatibility Assistant remembers executables it has prompted about,
    // keyed by full exe path. Entries for executables that no longer exist are dead.
    private const string CompatAssistantStoreKey =
        @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store";
    private const string CompatAssistantPersistedKey =
        @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Persisted";

    private static void ScanCompatibilityAssistant(ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        foreach (var keyPath in new[] { CompatAssistantStoreKey, CompatAssistantPersistedKey })
        {
            using var key = root.OpenSubKey(keyPath);
            if (key is null) continue;
            foreach (var valueName in key.GetValueNames())
            {
                // Value names are full executable paths; only act on absolute local
                // paths we can positively verify are gone.
                if (string.IsNullOrEmpty(valueName) || valueName.Contains('%') || !Path.IsPathFullyQualified(valueName)) continue;
                if (File.Exists(valueName)) continue;
                findings.Add(new RegistryFinding("HKCU", keyPath,
                    $"Compatibility record for a missing program ({Path.GetFileName(valueName)})", true, 75, "Compatibility Assistant", valueName));
            }
        }
    }

    // Localized UI resource cache (MUI). Windows regenerates it on demand, so the
    // whole key is a safe, self-healing cache to clear.
    private static void ScanMuiCache(ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var mui = root.OpenSubKey(@"Control Panel\Desktop\MuiCached");
        if (mui is null || mui.ValueCount == 0) return;
        findings.Add(new RegistryFinding("HKCU", @"Control Panel\Desktop\MuiCached",
            "Localized UI cache that Windows regenerates automatically", true, 80, "MUI Cache"));
    }

    // Current-user auto-start entries (Run / RunOnce) whose executable no longer
    // exists on disk. Deleting a startup entry whose program is gone cannot break
    // anything - the program is simply not there to run.
    private static void ScanStartupEntries(ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        foreach (var path in new[]
        {
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            @"Software\Microsoft\Windows\CurrentVersion\RunOnce"
        })
        {
            using var key = root.OpenSubKey(path);
            if (key is null) continue;
            foreach (var valueName in key.GetValueNames())
            {
                var command = key.GetValue(valueName) as string;
                if (string.IsNullOrWhiteSpace(command)) continue;
                var exe = ResolveStartupExecutable(command);
                if (exe is null || File.Exists(exe)) continue;
                findings.Add(new RegistryFinding("HKCU", path,
                    $"Startup entry '{valueName}' points to a missing executable", true, 70, "Windows Startup", valueName));
            }
        }
    }

    // Current-user sound event entries whose referenced sound file has been removed.
    // A dead reference is harmless to delete and only silences a missing sound.
    private static void ScanSoundAppEvents(ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var apps = root.OpenSubKey(@"AppEvents\Schemes\Apps");
        if (apps is null) return;
        foreach (var appName in apps.GetSubKeyNames())
        {
            using var appKey = apps.OpenSubKey(appName);
            if (appKey is null) continue;
            foreach (var eventName in appKey.GetSubKeyNames())
            {
                using var eventKey = appKey.OpenSubKey(eventName);
                var wav = eventKey?.GetValue(".Default") as string;
                if (string.IsNullOrWhiteSpace(wav) || wav.Contains('%') || !Path.IsPathFullyQualified(wav)) continue;
                if (!File.Exists(wav))
                    findings.Add(new RegistryFinding("HKCU", $@"AppEvents\Schemes\Apps\{appName}\{eventName}",
                        "Sound event references a missing file", true, 70, "Sound AppEvents", ".Default"));
            }
        }
    }

    // Shell MuiCache stores friendly display names for executables, keyed by the
    // executable's full path. Entries whose executable no longer exists are dead
    // cache; Windows rebuilds the cache on demand, so removing them is safe.
    private const string ShellMuiCacheKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";

    private static void ScanShellMuiCache(ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var key = root.OpenSubKey(ShellMuiCacheKey);
        if (key is null) return;
        foreach (var valueName in key.GetValueNames())
        {
            if (string.IsNullOrEmpty(valueName)) continue;
            // Value names look like "<full exe path>.FriendlyAppName" or
            // ".ApplicationCompany"; strip the known suffix to get the executable.
            var exe = valueName;
            foreach (var suffix in new[] { ".FriendlyAppName", ".ApplicationCompany" })
            {
                if (exe.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    exe = exe[..^suffix.Length];
                    break;
                }
            }
            // Only act on absolute paths we can positively verify are gone; env-var
            // and non-qualified entries are left alone.
            if (exe.Contains('%') || !Path.IsPathFullyQualified(exe)) continue;
            if (File.Exists(exe)) continue;
            findings.Add(new RegistryFinding("HKCU", ShellMuiCacheKey,
                $"Cached app name for a missing program ({Path.GetFileName(exe)})", true, 75, "Shell Cache", valueName));
        }
    }

    // Per-user App Paths entries whose target executable no longer exists on disk.
    // App Paths only resolves a program name to its full path; a dead entry does
    // nothing but point at a program that is gone.
    private const string AppPathsKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths";

    private static void ScanUserAppPaths(ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var appPaths = root.OpenSubKey(AppPathsKey);
        if (appPaths is null) return;
        foreach (var name in appPaths.GetSubKeyNames())
        {
            using var entry = appPaths.OpenSubKey(name);
            var target = entry?.GetValue(null) as string; // (Default) = executable path
            if (string.IsNullOrWhiteSpace(target)) continue;
            var exe = target.Trim('"');
            if (exe.Contains('%') || !Path.IsPathFullyQualified(exe)) continue;
            if (File.Exists(exe)) continue;
            findings.Add(new RegistryFinding("HKCU", $@"{AppPathsKey}\{name}",
                $"App Paths entry '{name}' points to a missing program", true, 75, "App Paths"));
        }
    }

    /// <summary>Extracts a fully-qualified executable path from a Run value, or null
    /// if the value cannot be resolved (env vars, relative paths, non-exe commands).
    /// Unknown/ambiguous values are never flagged so the cleaner only acts on entries
    /// it can positively verify as missing.</summary>
    internal static string? ResolveStartupExecutable(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0) return null;
        string? candidate = trimmed.StartsWith('"')
            ? (trimmed.IndexOf('"', 1) is var end && end > 1 ? trimmed[1..end] : null)
            : trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (candidate is null || candidate.Contains('%')) return null;
        return Path.IsPathFullyQualified(candidate) ? candidate : null;
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
        // IgnoreInaccessible keeps one access-denied subfolder from killing the walk.
        try { return FileEnumeration.Files(path).ToArray(); }
        catch { return []; }
    }

    private static long GetDirectorySize(string path)
        => EnumerateFiles(path).Sum(f => { try { return new FileInfo(f).Length; } catch { return 0; } });

    private static bool IsExcluded(string path, IReadOnlySet<string>? exclusions)
        => exclusions?.Any(root => NativeSafety.IsWithin(path, root)) == true;
}
