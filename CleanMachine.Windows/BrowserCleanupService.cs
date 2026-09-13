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
    private readonly CleanupService _cleanup = new();
    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CleanMachine", "browser-cleanup-state.json");

    public Task<IReadOnlyList<BrowserCleanupTarget>> ScanAsync(
        IEnumerable<string> browsers,
        CancellationToken token = default)
        => _cleanup.ScanBrowsersAsync(browsers, cancellationToken: token);

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
            .SelectMany(t => { try { return Directory.EnumerateFiles(t.Path, "*", SearchOption.AllDirectories); } catch { return []; } })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await SaveInterruptedStateAsync(
            new BrowserCleanupState(operationId, files, 0, 0, DateTimeOffset.UtcNow), token);

        var report = await _cleanup.CleanBrowserTargetsAsync(allowed, progress, options.SecureDelete, token);
        await ClearStateAsync(token);
        return report;
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
        catch (JsonException) { return null; }
    }

    public async Task SaveInterruptedStateAsync(BrowserCleanupState state, CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temp = _statePath + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, state with { UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken: token);
        File.Move(temp, _statePath, true);
    }

    public async Task ClearStateAsync(CancellationToken token = default)
    {
        await Task.CompletedTask;
        try { if (File.Exists(_statePath)) File.Delete(_statePath); } catch (IOException) { }
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
    /// The browser must be closed; one item failing never aborts the rest.</summary>
    public Task<CleanupReport> CleanItemsAsync(
        IEnumerable<(string BrowserId, string ItemId)> selection,
        SecureDeleteOptions? secureDelete = null,
        CancellationToken token = default)
    {
        var running = GetRunningBrowsers();
        if (running.Count > 0)
            throw new InvalidOperationException(
                $"Close these browsers before cleaning: {string.Join(", ", running)}.");

        var removed = 0;
        long bytes = 0;
        var skipped = new List<CleanupIssue>();

        foreach (var (browserId, itemId) in selection.Distinct())
        {
            token.ThrowIfCancellationRequested();
            var browser = BrowserCatalog.Find(browserId);
            if (browser is null || !BrowserCatalog.IsInstalled(browser)) continue;

            var profiles = BrowserCatalog.Profiles(browser);
            var userData = browser.UserDataRoots.Where(Directory.Exists).ToArray();

            if (BrowserCatalog.IsPreferenceEdit(itemId))
            {
                ApplyPreferenceEdit(browser, profiles, skipped);
                continue;
            }

            foreach (var path in ResolvePaths(browser, itemId, profiles, userData))
            {
                var result = DeletePath(path, secureDelete);
                removed += result.Removed;
                bytes += result.Bytes;
                skipped.AddRange(result.Skipped);
            }
        }
        return Task.FromResult(new CleanupReport(new CleanupResult(removed, bytes), skipped));
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
            if (Directory.Exists(path)) return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToArray();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return [];
    }

    private static (int Removed, long Bytes, List<CleanupIssue> Skipped) DeletePath(string path, SecureDeleteOptions? secureDelete = null)
    {
        var removed = 0;
        long bytes = 0;
        var skipped = new List<CleanupIssue>();
        try
        {
            if (File.Exists(path))
            {
                var length = new FileInfo(path).Length;
                if (secureDelete is not null)
                    _ = SecureDeleteService.SecureDeleteFileAsync(path, secureDelete).GetAwaiter().GetResult();
                else
                    File.Delete(path);
                return (1, length, skipped);
            }
            if (!Directory.Exists(path)) return (0, 0, skipped);

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var length = new FileInfo(file).Length;
                    if (secureDelete is not null)
                        _ = SecureDeleteService.SecureDeleteFileAsync(file, secureDelete).GetAwaiter().GetResult();
                    else
                        File.Delete(file);
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
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
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
    private static void ApplyPreferenceEdit(BrowserDefinition browser, IReadOnlyList<string> profiles, List<CleanupIssue> skipped)
    {
        foreach (var profile in profiles)
        {
            if (browser.Family == BrowserFamily.Chromium)
            {
                var preferences = Path.Combine(profile, "Preferences");
                if (File.Exists(preferences)) TryRemoveJsonKeys(preferences, "download", ["default_directory", "directory_upgrade"], skipped);
            }
            else if (browser.Family == BrowserFamily.Firefox)
            {
                var prefs = Path.Combine(profile, "prefs.js");
                if (File.Exists(prefs)) TryRemovePrefsJsLines(prefs, skipped);
            }
        }
    }

    private static void TryRemoveJsonKeys(string path, string section, string[] keys, List<CleanupIssue> skipped)
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root?[section] is not JsonObject sectionObject) return;

            var changed = keys.Aggregate(false, (current, key) => sectionObject.Remove(key) || current);
            if (!changed) return;

            File.Copy(path, path + ".cleanmachine.bak", true);
            File.WriteAllText(path, root.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            skipped.Add(new CleanupIssue(path, ex.Message));
        }
    }

    private static void TryRemovePrefsJsLines(string path, List<CleanupIssue> skipped)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            var filtered = lines
                .Where(line => !line.Contains("browser.download.dir", StringComparison.Ordinal)
                    && !line.Contains("browser.download.lastDir", StringComparison.Ordinal))
                .ToArray();
            if (filtered.Length == lines.Length) return;

            File.Copy(path, path + ".cleanmachine.bak", true);
            File.WriteAllLines(path, filtered);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped.Add(new CleanupIssue(path, ex.Message));
        }
    }
}
