using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CleanMachine.Windows;

public sealed record BrowserCleanupOptions(
    IReadOnlySet<string>? ExcludedPaths = null,
    IReadOnlyList<string>? AdditionalProfileRoots = null,
    bool RequireBrowsersClosed = true,
    SecureDeleteOptions? SecureDelete = null);

public sealed record BrowserCleanupState(
    string OperationId,
    IReadOnlyList<string> RemainingFiles,
    int Removed,
    long BytesRecovered,
    DateTimeOffset UpdatedAt);

/// <summary>A cleanable item for one browser, sized for the UI.</summary>
public sealed record BrowserItemInfo(string Id, string Name, bool Destructive, long Bytes, int FileCount, string Description);

/// <summary>Detection result for one catalog browser.</summary>
public sealed record BrowserScan(string Id, string Name, bool Installed, IReadOnlyList<BrowserItemInfo> Items);

public sealed class BrowserCleanupService
{
    private static readonly SemaphoreSlim StateGate = new(1, 1);
    private readonly CleanupService _cleanup = new();
    private readonly string _statePath = Path.Combine(
        AppDataPaths.Root, "browser-cleanup-state.json");

    public Task<IReadOnlyList<BrowserCleanupTarget>> ScanAsync(
        IEnumerable<string> browsers,
        IEnumerable<string>? additionalRoots = null,
        IReadOnlySet<string>? excludedPaths = null,
        CancellationToken token = default)
        => _cleanup.ScanBrowsersAsync(browsers, additionalRoots, excludedPaths, token);

    public async Task<CleanupReport> CleanWithReportAsync(
        IEnumerable<BrowserCleanupTarget> targets,
        BrowserCleanupOptions? options = null,
        IProgress<CleanupProgress>? progress = null,
        CancellationToken token = default)
    {
        using var cleaning = CleaningActivity.Begin();
        await CleanupCoordinator.Gate.WaitAsync(token);
        try
        {
            options ??= new BrowserCleanupOptions();
            if (options.RequireBrowsersClosed)
            {
                var running = GetRunningBrowsers();
                if (running.Count > 0)
                    throw new InvalidOperationException(
                        $"Close these browsers before cleaning: {string.Join(", ", running)}.");
            }

            var allowed = targets
                .Where(t => t.Selected && !IsExcluded(t.Path, options.ExcludedPaths))
                .ToArray();

            var operationId = Guid.NewGuid().ToString("N");
            var files = allowed
                .SelectMany(t => FileEnumeration.Files(t.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            await SaveInterruptedStateAsync(
                new BrowserCleanupState(operationId, files, 0, 0, DateTimeOffset.UtcNow), token);

            var report = await _cleanup.CleanBrowserTargetsAsync(allowed, progress, options.SecureDelete, token);
            await ClearStateAsync(token);
            return report;
        }
        finally
        {
            CleanupCoordinator.Gate.Release();
        }
    }

    public async Task<CleanupResult> CleanAsync(
        IEnumerable<BrowserCleanupTarget> targets,
        bool requireBrowsersClosed = true,
        CancellationToken token = default)
        => (await CleanWithReportAsync(targets, new BrowserCleanupOptions(RequireBrowsersClosed: requireBrowsersClosed), null, token)).Result;

    public async Task<BrowserCleanupState?> LoadInterruptedStateAsync(CancellationToken token = default)
    {
        try
        {
            if (!File.Exists(_statePath)) return null;
            await using var stream = File.OpenRead(_statePath);
            return await JsonSerializer.DeserializeAsync<BrowserCleanupState>(stream, cancellationToken: token);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    public async Task SaveInterruptedStateAsync(BrowserCleanupState state, CancellationToken token = default)
    {
        await StateGate.WaitAsync(token);
        var temp = $"{_statePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            await using (var stream = File.Create(temp))
                await JsonSerializer.SerializeAsync(stream, state with { UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken: token);
            File.Move(temp, _statePath, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            StateGate.Release();
        }
    }

    public async Task ClearStateAsync(CancellationToken token = default)
    {
        await StateGate.WaitAsync(token);
        try { if (File.Exists(_statePath)) File.Delete(_statePath); }
        catch (IOException) { }
        // Access-denied must be swallowed too: this runs right after a successful
        // clean, and letting it escape would discard the report the caller is about
        // to return (the same handling SaveInterruptedStateAsync already applies).
        catch (UnauthorizedAccessException) { }
        finally { StateGate.Release(); }
    }

    public static IReadOnlyList<string> GetRunningBrowsers()
    {
        var result = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            try { if (process.ProcessName is "chrome" or "msedge" or "firefox") result.Add(process.ProcessName); }
            catch { }
            finally { process.Dispose(); }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>User-facing name for a browser process ("msedge" -> "Microsoft Edge").
    /// Unknown process names fall back to the name itself.</summary>
    public static string DisplayNameForProcess(string processName)
        => BrowserCatalog.Browsers
            .FirstOrDefault(b => b.ProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase))?.Name
            ?? processName;

    /// <summary>Two-tier close-assist for the manual cleaning flow: asks every running
    /// supported browser to close politely (the same close the user gets by clicking
    /// the window's X, so sessions and recent-tabs lists stay intact), waits up to
    /// five seconds, then force-kills only whatever is still alive. Returns the
    /// process names that are STILL running afterwards; an empty result means all
    /// browsers closed. Never touches other applications.</summary>
    public static async Task<IReadOnlyList<string>> CloseRunningBrowsersAsync(
        IEnumerable<string>? processNames = null,
        CancellationToken token = default)
    {
        var names = (processNames ?? GetRunningBrowsers())
            .Where(n => BrowserCatalog.Browsers.Any(b => b.ProcessNames.Contains(n, StringComparer.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0) return [];

        // Tier 1: graceful. WM_CLOSE asks the app to close like the window's X
        // button; browsers with background mode (Chrome's "Continue running
        // background apps", Edge's "Startup Boost") may keep processes alive
        // after closing their windows, which tier 2 handles.
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!names.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase)) continue;
                // WM_CLOSE closes windows politely; return value is false when the
                // process has no window (background mode) - tier 2 covers it.
                process.CloseMainWindow();
            }
            catch { }
            finally { process.Dispose(); }
        }

        // Give the graceful close a moment to work before escalating.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            var stillRunning = GetRunningBrowsers().Where(n => names.Contains(n, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (stillRunning.Length == 0) return [];
            await Task.Delay(250, token);
        }

        // Tier 2: force. Only the browsers we were asked to close, only if still
        // alive after the graceful window expired.
        foreach (var name in names)
            foreach (var process in Process.GetProcessesByName(name))
            {
                try { process.Kill(entireProcessTree: true); }
                catch { } finally { process.Dispose(); }
            }

        // Wait for the kills to land, then report anything that survived.
        var postKillDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < postKillDeadline)
        {
            token.ThrowIfCancellationRequested();
            var stillRunning = GetRunningBrowsers().Where(n => names.Contains(n, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (stillRunning.Length == 0) return [];
            await Task.Delay(250, token);
        }
        return GetRunningBrowsers().Where(n => names.Contains(n, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    private static bool IsExcluded(string path, IReadOnlySet<string>? exclusions)
        => exclusions?.Any(root => NativeSafety.IsWithin(path, root)) == true;

    // ---- Item-based detection, scanning, and cleaning --------------------------

    /// <summary>Detects every catalog browser and, for installed ones, sizes each
    /// cleanable item. Runs on a background thread; cache enumeration can take a moment.</summary>
    public Task<IReadOnlyList<BrowserScan>> DetectAndScanAsync(CancellationToken token = default)
        => Task.Run<IReadOnlyList<BrowserScan>>(() => BrowserCatalog.Browsers.Select(ScanBrowser).ToArray(), token);

    private static BrowserScan ScanBrowser(BrowserDefinition browser)
    {
        var installed = BrowserCatalog.IsInstalled(browser);
        var profiles = installed ? BrowserCatalog.Profiles(browser) : [];
        var userData = installed ? browser.UserDataRoots.Where(Directory.Exists).ToArray() : [];
        var items = new List<BrowserItemInfo>();

        foreach (var definition in BrowserCatalog.ItemsFor(browser.Family))
        {
            long bytes = 0;
            var files = 0;
            if (installed && !BrowserCatalog.IsPreferenceEdit(definition.Id))
            {
                foreach (var file in ResolvePaths(browser, definition.Id, profiles, userData).SelectMany(EnumerateFiles))
                {
                    try { bytes += new FileInfo(file).Length; files++; }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            items.Add(new BrowserItemInfo(definition.Id, definition.Name, definition.Destructive, bytes, files, definition.Description));
        }
        return new BrowserScan(browser.Id, browser.Name, installed, items);
    }

    /// <summary>Cleans the selected (browser, item) pairs using whole-file deletion.
    /// The browser must be closed unless <paramref name="requireBrowsersClosed"/> is
    /// false - the browser-exit monitor uses that because it fires right after its
    /// browser closed, while another browser may still be open (its files belong to
    /// it and are not touched). One item failing never aborts the rest.</summary>
    public async Task<CleanupReport> CleanItemsAsync(
        IEnumerable<(string BrowserId, string ItemId)> selection,
        SecureDeleteOptions? secureDelete = null,
        CancellationToken token = default,
        bool requireBrowsersClosed = true)
    {
        using var cleaning = CleaningActivity.Begin();
        await CleanupCoordinator.Gate.WaitAsync(token);
        try
        {
            var running = GetRunningBrowsers();
            if (requireBrowsersClosed && running.Count > 0)
                throw new InvalidOperationException(
                    $"Close these browsers before cleaning: {string.Join(", ", running)}.");

            // Deleting (and optionally multi-pass overwriting) the selected items is
            // long-running disk work; keep it off the caller's (UI) thread.
            return await Task.Run(async () =>
            {
                var removed = 0;
                long bytes = 0;
                var skipped = new List<CleanupIssue>();
                var cleanedItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var (browserId, itemId) in selection.Distinct())
                {
                    token.ThrowIfCancellationRequested();
                    var browser = BrowserCatalog.Find(browserId);
                    if (browser is null || !BrowserCatalog.IsInstalled(browser)) continue;

                    var profiles = BrowserCatalog.Profiles(browser);
                    var userData = browser.UserDataRoots.Where(Directory.Exists).ToArray();

                    if (BrowserCatalog.IsPreferenceEdit(itemId))
                    {
                        if (ApplyPreferenceEdit(browser, profiles, skipped))
                        {
                            removed++;
                            cleanedItems.Add($"{browserId}:{itemId}");
                        }
                        continue;
                    }

                    foreach (var path in ResolvePaths(browser, itemId, profiles, userData))
                    {
                        var result = await DeletePathAsync(path, secureDelete, token);
                        removed += result.Removed;
                        bytes += result.Bytes;
                        skipped.AddRange(result.Skipped);
                        if (result.Removed > 0)
                            cleanedItems.Add($"{browserId}:{itemId}");
                    }
                }
                return new CleanupReport(new CleanupResult(removed, bytes), skipped, CleanedPaths: cleanedItems);
            }, token);
        }
        finally
        {
            CleanupCoordinator.Gate.Release();
        }
    }

    private static IReadOnlyList<string> ResolvePaths(
        BrowserDefinition browser, string itemId, IReadOnlyList<string> profiles, IReadOnlyList<string> userDataRoots)
    {
        var paths = new List<string>();
        foreach (var mapped in BrowserCatalog.PathsFor(browser.Family, itemId))
        {
            switch (mapped.Root)
            {
                case BrowserItemRoot.Absolute: paths.Add(mapped.Relative); break;
                case BrowserItemRoot.Profile: paths.AddRange(profiles.Select(p => Path.Combine(p, mapped.Relative))); break;
                case BrowserItemRoot.UserData: paths.AddRange(userDataRoots.Select(u => Path.Combine(u, mapped.Relative))); break;
            }
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> EnumerateFiles(string path)
    {
        try
        {
            if (File.Exists(path)) return [path];
            // Deliberately lazy: callers size everything (ScanBrowser) or stop early
            // at a UI cap (ListItemFiles), so materialising the whole tree here would
            // walk tens of thousands of cache files even when only the first few
            // hundred are needed.
            return FileEnumeration.Files(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return [];
    }

    /// <summary>The individual files an item's clean would remove for a browser, with
    /// sizes, for the detail drill-down. An item can map to several roots (cache
    /// spans Cache, Code Cache, GPUCache, ... across every profile), so this flattens
    /// them all into one list.</summary>
    public IReadOnlyList<CleanupFileDetail> ListItemFiles(string browserId, string itemId, int max = 500)
    {
        var browser = BrowserCatalog.Find(browserId);
        if (browser is null || !BrowserCatalog.IsInstalled(browser)) return [];
        var profiles = BrowserCatalog.Profiles(browser);
        var userData = browser.UserDataRoots.Where(Directory.Exists).ToArray();
        var files = new List<CleanupFileDetail>();
        foreach (var path in ResolvePaths(browser, itemId, profiles, userData))
        {
            // This is called from UI click handlers; EnumerateFiles materialises the
            // whole subtree for a directory, so a large cache (tens of thousands of
            // files) is walked in full just to render a few hundred rows. Stop
            // enumerating as soon as the cap is reached.
            if (files.Count >= max) return files;
            foreach (var file in EnumerateFiles(path))
            {
                try { files.Add(new CleanupFileDetail(file, new FileInfo(file).Length)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                if (files.Count >= max) return files; // cap the UI drill-down; the scan carries the true total
            }
        }
        return files;
    }

    private static async Task<(int Removed, long Bytes, List<CleanupIssue> Skipped)> DeletePathAsync(
        string path, SecureDeleteOptions? secureDelete = null, CancellationToken token = default)
    {
        var removed = 0;
        long bytes = 0;
        var skipped = new List<CleanupIssue>();
        try
        {
            if (File.Exists(path))
            {
                var length = new FileInfo(path).Length;
                if (secureDelete is not null
                    && !await SecureDeleteService.SecureDeleteFileAsync(path, secureDelete, token))
                {
                    skipped.Add(new CleanupIssue(path, "Protected, locked, or empty - not securely deleted"));
                    return (0, 0, skipped);
                }
                if (secureDelete is null) File.Delete(path);
                return (1, length, skipped);
            }
            if (!Directory.Exists(path)) return (0, 0, skipped);

            foreach (var file in FileEnumeration.Files(path))
            {
                try
                {
                    var length = new FileInfo(file).Length;
                    if (secureDelete is not null
                        && !await SecureDeleteService.SecureDeleteFileAsync(file, secureDelete, token))
                    {
                        skipped.Add(new CleanupIssue(file, "Protected, locked, or empty - not securely deleted"));
                        continue;
                    }
                    if (secureDelete is null) File.Delete(file);
                    removed++;
                    bytes += length;
                }
                catch (IOException) { skipped.Add(new CleanupIssue(file, "File is locked or unavailable")); }
                catch (UnauthorizedAccessException) { skipped.Add(new CleanupIssue(file, "Access denied")); }
            }
            RemoveEmptyDirectories(path);
        }
        catch (IOException) { skipped.Add(new CleanupIssue(path, "Locked or unavailable")); }
        catch (UnauthorizedAccessException) { skipped.Add(new CleanupIssue(path, "Access denied")); }
        return (removed, bytes, skipped);
    }

    private static void RemoveEmptyDirectories(string root)
    {
        try
        {
            foreach (var directory in FileEnumeration.Directories(root).OrderByDescending(d => d.Length))
            {
                try { if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Last Download Location lives in a settings file, not a data file, so it
    /// is edited in place (with a backup) rather than deleted.</summary>
    private static bool ApplyPreferenceEdit(BrowserDefinition browser, IReadOnlyList<string> profiles, List<CleanupIssue> skipped)
    {
        var changed = false;
        foreach (var profile in profiles)
        {
            if (browser.Family == BrowserFamily.Chromium)
            {
                var preferences = Path.Combine(profile, "Preferences");
                if (File.Exists(preferences))
                    changed |= TryRemoveJsonKeys(preferences, "download", ["default_directory", "directory_upgrade"], skipped);
            }
            else if (browser.Family == BrowserFamily.Firefox)
            {
                var prefs = Path.Combine(profile, "prefs.js");
                if (File.Exists(prefs))
                    changed |= TryRemovePrefsJsLines(prefs, skipped);
            }
        }
        return changed;
    }

    private static bool TryRemoveJsonKeys(string path, string section, string[] keys, List<CleanupIssue> skipped)
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root?[section] is not JsonObject sectionObject) return false;

            var changed = keys.Aggregate(false, (current, key) => sectionObject.Remove(key) || current);
            if (!changed) return false;

            File.Copy(path, path + ".cleanmachine.bak", true);
            File.WriteAllText(path, root.ToJsonString());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            skipped.Add(new CleanupIssue(path, ex.Message));
            return false;
        }
    }

    private static bool TryRemovePrefsJsLines(string path, List<CleanupIssue> skipped)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            var filtered = lines
                .Where(line => !line.Contains("browser.download.dir", StringComparison.Ordinal)
                    && !line.Contains("browser.download.lastDir", StringComparison.Ordinal))
                .ToArray();
            if (filtered.Length == lines.Length) return false;

            File.Copy(path, path + ".cleanmachine.bak", true);
            File.WriteAllLines(path, filtered);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped.Add(new CleanupIssue(path, ex.Message));
            return false;
        }
    }
}
