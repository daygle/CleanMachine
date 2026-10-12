using System.Diagnostics;
using System.Text.Json.Nodes;

namespace CleanMachine.Windows;

/// <summary>A cleanable item for one browser, sized for the UI.</summary>
public sealed record BrowserItemInfo(string Id, string Name, bool Destructive, long Bytes, int FileCount, string Description);

/// <summary>Detection result for one catalog browser.</summary>
public sealed record BrowserScan(string Id, string Name, bool Installed, IReadOnlyList<BrowserItemInfo> Items);

public sealed class BrowserCleanupService
{
    /// <summary>The interrupted-state file an older cleanup path wrote before every
    /// Quick Clean or scheduled browser clean. Nothing writes it any more; it is
    /// only named so <see cref="RemoveLegacyStateFile"/> can sweep a stale copy.</summary>
    private static string LegacyStatePath => Path.Combine(AppDataPaths.Root, "browser-cleanup-state.json");

    /// <summary>Best-effort removal of the obsolete interrupted-state file. It could
    /// list every cache file of a run, so it is not worth leaving behind.</summary>
    internal static void RemoveLegacyStateFile()
    {
        try { if (File.Exists(LegacyStatePath)) File.Delete(LegacyStatePath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Process names of every catalog browser. Derived from the catalog so
    /// the "close your browsers first" check covers every browser the cleaner can
    /// touch - a hand-kept list of three let Brave, Vivaldi and Opera be cleaned
    /// while they were running.</summary>
    private static readonly string[] BrowserProcessNames = BrowserCatalog.Browsers
        .SelectMany(b => b.ProcessNames)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>Process names of the given catalog browsers (unknown ids are ignored).</summary>
    internal static IReadOnlyList<string> ProcessNamesFor(IEnumerable<string> browserIds)
        => browserIds
            .Select(BrowserCatalog.Find)
            .OfType<BrowserDefinition>()
            .SelectMany(b => b.ProcessNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>Which of <paramref name="processNames"/> (every catalog browser when
    /// null) are running right now.</summary>
    public static IReadOnlyList<string> GetRunningBrowsers(IEnumerable<string>? processNames = null)
    {
        // Query each browser by name rather than sweeping Process.GetProcesses():
        // the latter materialises a Process for every running process just to
        // filter down to a handful. This only touches the browsers we care about.
        var result = new List<string>();
        foreach (var name in processNames ?? BrowserProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length > 0) result.Add(name);
            }
            finally
            {
                foreach (var process in processes) { try { process.Dispose(); } catch { } }
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>User-facing name for a browser process ("msedge" -> "Microsoft Edge").
    /// Several browsers can share a process name (chrome.exe is Google Chrome and
    /// Chromium), so when <paramref name="amongBrowserIds"/> is given the name comes
    /// from those browsers - the ones being cleaned - first. Unknown process names
    /// fall back to the name itself.</summary>
    public static string DisplayNameForProcess(string processName, IEnumerable<string>? amongBrowserIds = null)
    {
        var matches = BrowserCatalog.Browsers
            .Where(b => b.ProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (amongBrowserIds is not null)
        {
            var preferred = matches
                .Where(b => amongBrowserIds.Contains(b.Id, StringComparer.OrdinalIgnoreCase))
                .Select(b => b.Name)
                .ToList();
            if (preferred.Count > 0) return string.Join(" / ", preferred);
        }
        return matches.FirstOrDefault()?.Name ?? processName;
    }

    /// <summary>Close-assist for the manual cleaning flow, used only after the user
    /// confirms. Asks each requested browser to close politely (the same close the
    /// user gets by clicking the window's X, so sessions and recent-tabs lists stay
    /// intact) and waits up to five seconds.
    /// <para>Windowless processes can still be uploading or holding unsaved data,
    /// so even background leftovers are never killed.</para>
    /// Returns the process names that are STILL running afterwards; an empty result
    /// means every requested browser closed. Never touches other applications.</summary>
    public static async Task<IReadOnlyList<string>> CloseRunningBrowsersAsync(
        IEnumerable<string>? processNames = null,
        CancellationToken token = default)
    {
        var names = (processNames ?? GetRunningBrowsers())
            .Where(n => BrowserCatalog.Browsers.Any(b => b.ProcessNames.Contains(n, StringComparer.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0) return [];

        // Graceful: WM_CLOSE asks the browser to close like the window's X button.
        // Each requested browser is queried by name rather than sweeping every
        // process on the machine.
        foreach (var name in names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try { process.CloseMainWindow(); }
                catch { }
                finally { process.Dispose(); }
            }
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (GetRunningBrowsers(names).Count == 0) return [];
            await Task.Delay(250, token);
        }

        // No force-kill, even for a process with no visible main window: it may
        // be uploading data or have a hidden window/prompt. The user must close
        // leftovers using the browser's own controls before destructive cleaning.
        return GetRunningBrowsers(names);
    }

    internal static bool MayEndWithoutLosingWork(bool hasOpenWindow) => false;

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
    /// false - Quick Clean / scheduled runs use that because they only clear
    /// caches. The browser-exit monitor rechecks only its selected browser, since
    /// the user may opt in to destructive items and the browser may have reopened.
    /// One item failing never aborts the rest.
    /// <para>Anything under <paramref name="excludedPaths"/> (the user's Settings
    /// exclusions) is never deleted. <paramref name="skipModifiedWithin"/> leaves
    /// files written that recently alone: a run that does not require the browser
    /// to be closed passes it so a cache file the browser is using right now is
    /// not pulled out from under it.</para></summary>
    public async Task<CleanupReport> CleanItemsAsync(
        IEnumerable<(string BrowserId, string ItemId)> selection,
        CancellationToken token = default,
        bool requireBrowsersClosed = true,
        IReadOnlySet<string>? excludedPaths = null,
        TimeSpan? skipModifiedWithin = null)
    {
        var selected = selection.Distinct().ToArray();
        using var cleaning = CleaningActivity.Begin();
        await CleanupCoordinator.Gate.WaitAsync(token);
        try
        {
            if (requireBrowsersClosed)
            {
                // Only the browsers being cleaned have to be closed: each item's
                // files belong to its own browser, so another browser can stay open
                // (and in use) while this one is cleaned.
                var selectedBrowsers = selected.Select(s => s.BrowserId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var running = GetRunningBrowsers(ProcessNamesFor(selectedBrowsers));
                if (running.Count > 0)
                    throw new InvalidOperationException(
                        $"Close these browsers before cleaning: {string.Join(", ", running.Select(p => DisplayNameForProcess(p, selectedBrowsers)).Distinct())}.");
            }

            // Deleting the selected items is long-running disk work; keep it off
            // the caller's (UI) thread.
            return await CleanupJournal.RunAsync("Browser Cleanup", () => Task.Run(() =>
            {
                var removed = 0;
                long bytes = 0;
                var skipped = new List<CleanupIssue>();
                var cleanedItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var modifiedCutoff = skipModifiedWithin is { } window ? DateTime.UtcNow - window : (DateTime?)null;

                foreach (var (browserId, requestedItemId) in selected)
                {
                    token.ThrowIfCancellationRequested();
                    var browser = BrowserCatalog.Find(browserId);
                    if (browser is null || !BrowserCatalog.IsInstalled(browser)) continue;

                    var definition = BrowserCatalog.ItemsFor(browser.Family)
                        .FirstOrDefault(i => i.Id.Equals(requestedItemId, StringComparison.OrdinalIgnoreCase));
                    if (definition is null) continue;
                    var itemId = definition.Id;
                    if (browser.Family == BrowserFamily.Firefox && itemId is "history" or "download-history")
                    {
                        skipped.Add(new(browserId + ":" + itemId, "Use Firefox to clear history without deleting bookmarks."));
                        continue;
                    }
                    if (!requireBrowsersClosed && definition.Destructive
                        && GetRunningBrowsers(browser.ProcessNames).Count > 0)
                    {
                        skipped.Add(new(browserId + ":" + itemId, "Browser reopened; saved data was not touched."));
                        continue;
                    }
                    var profiles = BrowserCatalog.Profiles(browser);
                    var userData = browser.UserDataRoots.Where(Directory.Exists).ToArray();

                    if (BrowserCatalog.IsPreferenceEdit(itemId))
                    {
                        if (ApplyPreferenceEdit(browser, profiles, skipped, excludedPaths))
                        {
                            CleanupJournal.Record(1, 0);
                            removed++;
                            cleanedItems.Add($"{browserId}:{itemId}");
                        }
                        continue;
                    }

                    foreach (var path in ResolvePaths(browser, itemId, profiles, userData))
                    {
                        token.ThrowIfCancellationRequested();
                        var result = DeletePath(path, excludedPaths, modifiedCutoff, token);
                        removed += result.Removed;
                        bytes += result.Bytes;
                        skipped.AddRange(result.Skipped);
                        if (result.Removed > 0)
                            cleanedItems.Add($"{browserId}:{itemId}");
                    }
                }
                return new CleanupReport(new CleanupResult(removed, bytes), skipped, CleanedPaths: cleanedItems);
            }, token));
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
            // EnumerateFiles is lazy: stop at the cap rather than walking an
            // entire large cache just to show a few hundred rows.
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

    internal static (int Removed, long Bytes, List<CleanupIssue> Skipped) DeletePath(
        string path, IReadOnlySet<string>? excludedPaths, DateTime? modifiedCutoff, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var removed = 0;
        long bytes = 0;
        var skipped = new List<CleanupIssue>();
        if (IsExcluded(path, excludedPaths)) return (0, 0, skipped);
        try
        {
            // Refuse to delete through a junction/symlink. The recursive walker below
            // already skips reparse points inside the tree, but the top-level path (and
            // a single-file target) is not covered by that, so prove it is a real entry
            // before removing it. Fails closed: an uninspectable path counts as a link.
            if (File.Exists(path) && NativeSafety.IsReparsePoint(path))
            {
                skipped.Add(new CleanupIssue(path, "Reparse point (junction/symlink) - skipped"));
                return (0, 0, skipped);
            }
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                if (modifiedCutoff is { } fileCutoff && info.LastWriteTimeUtc > fileCutoff)
                {
                    skipped.Add(new CleanupIssue(path, "Recently modified"));
                    return (0, 0, skipped);
                }
                var length = SafeDeletion.DeleteFile(path, path, modifiedCutoff);
                CleanupJournal.Record(1, length);
                return (1, length, skipped);
            }
            if (!Directory.Exists(path)) return (0, 0, skipped);
            if (NativeSafety.IsReparsePoint(path))
            {
                skipped.Add(new CleanupIssue(path, "Reparse point (junction/symlink) - skipped"));
                return (0, 0, skipped);
            }

            foreach (var file in FileEnumeration.Files(path, token))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (IsExcluded(file, excludedPaths)) continue;
                    if (NativeSafety.IsReparsePoint(file))
                    {
                        skipped.Add(new CleanupIssue(file, "Reparse point (junction/symlink) - skipped"));
                        continue;
                    }
                    var info = new FileInfo(file);
                    if (modifiedCutoff is { } cutoff && info.LastWriteTimeUtc > cutoff)
                    {
                        skipped.Add(new CleanupIssue(file, "Recently modified"));
                        continue;
                    }
                    var length = SafeDeletion.DeleteFile(file, path, modifiedCutoff);
                    CleanupJournal.Record(1, length);
                    removed++;
                    bytes += length;
                }
                catch (IOException) { skipped.Add(new CleanupIssue(file, "File is locked or unavailable")); }
                catch (UnauthorizedAccessException) { skipped.Add(new CleanupIssue(file, "Access denied")); }
            }
            RemoveEmptyDirectories(path, excludedPaths, token);
        }
        catch (IOException) { skipped.Add(new CleanupIssue(path, "Locked or unavailable")); }
        catch (UnauthorizedAccessException) { skipped.Add(new CleanupIssue(path, "Access denied")); }
        return (removed, bytes, skipped);
    }

    /// <summary>Deletes directories left empty by the clean, deepest first. Ordered
    /// by path LENGTH as a proxy for depth: every descendant string is longer than
    /// its ancestor, so a child is always visited before the parent that would
    /// otherwise still look non-empty. A parent whose child was not removed (locked)
    /// simply is not empty yet and is left alone.</summary>
    private static void RemoveEmptyDirectories(string root, IReadOnlySet<string>? excludedPaths, CancellationToken token)
    {
        try
        {
            foreach (var directory in FileEnumeration.Directories(root, token).OrderByDescending(d => d.Length))
            {
                token.ThrowIfCancellationRequested();
                if (IsExcluded(directory, excludedPaths) || NativeSafety.IsReparsePoint(directory)) continue;
                try { if (!Directory.EnumerateFileSystemEntries(directory).Any()) SafeDeletion.DeleteEmptyDirectory(directory, root); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (!IsExcluded(root, excludedPaths) && !NativeSafety.IsReparsePoint(root)
                && !Directory.EnumerateFileSystemEntries(root).Any()) SafeDeletion.DeleteEmptyDirectory(root, root);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Last Download Location lives in a settings file, not a data file, so it
    /// is edited in place (with a backup) rather than deleted.</summary>
    internal static bool ApplyPreferenceEdit(BrowserDefinition browser, IReadOnlyList<string> profiles, List<CleanupIssue> skipped, IReadOnlySet<string>? excludedPaths)
    {
        var changed = false;
        foreach (var profile in profiles)
        {
            if (browser.Family == BrowserFamily.Chromium)
            {
                var preferences = Path.Combine(profile, "Preferences");
                if (File.Exists(preferences) && !IsExcluded(preferences, excludedPaths) && !NativeSafety.IsReparsePoint(preferences))
                    changed |= TryRemoveJsonKeys(preferences, "download", ["default_directory", "directory_upgrade"], skipped);
            }
            else if (browser.Family == BrowserFamily.Firefox)
            {
                var prefs = Path.Combine(profile, "prefs.js");
                if (File.Exists(prefs) && !IsExcluded(prefs, excludedPaths) && !NativeSafety.IsReparsePoint(prefs))
                    changed |= TryRemovePrefsJsLines(prefs, skipped);
            }
        }
        return changed;
    }

    internal const int MaxPreferenceBytes = 8 * 1024 * 1024;
    internal static string ReadPreferenceText(string path)
    {
        using var lease = OperatingSystem.IsWindows() ? SafeDeletion.Pin(path) : null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxPreferenceBytes) throw new InvalidDataException("Preference file exceeds the size limit.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static bool TryRemoveJsonKeys(string path, string section, string[] keys, List<CleanupIssue> skipped)
    {
        var temp = path + $".{Guid.NewGuid():N}.cleanmachine.tmp";
        try
        {
            using var directoryLease = OperatingSystem.IsWindows() ? SafeDeletion.Pin(Path.GetDirectoryName(path)!) : null;
            var root = JsonNode.Parse(ReadPreferenceText(path)) as JsonObject;
            if (root?[section] is not JsonObject sectionObject) return false;

            var changed = keys.Aggregate(false, (current, key) => sectionObject.Remove(key) || current);
            if (!changed) return false;

            var backup = path + ".cleanmachine.bak";
            if (File.Exists(backup) && NativeSafety.IsReparsePoint(backup))
                throw new IOException("Preference backup is a reparse point.");
            if (File.Exists(backup)) SafeDeletion.DeleteFile(backup, Path.GetDirectoryName(path)!);
            File.Copy(path, backup, overwrite: false);
            // Write the whole document to a temp file and swap it in atomically, so a
            // crash or power loss mid-write can never leave a half-written Preferences
            // that the browser would then discard (losing far more than we removed).
            File.WriteAllText(temp, root.ToJsonString());
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            skipped.Add(new CleanupIssue(path, ex.Message));
            return false;
        }
        finally
        {
            // A failed or abandoned write must not leave a stray temp behind; on the
            // success path the Move above already consumed it.
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static bool IsDownloadDirectoryPreference(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("user_pref(\"browser.download.dir\",", StringComparison.Ordinal)
            || trimmed.StartsWith("user_pref(\"browser.download.lastDir\",", StringComparison.Ordinal);
    }

    private static bool TryRemovePrefsJsLines(string path, List<CleanupIssue> skipped)
    {
        var temp = path + $".{Guid.NewGuid():N}.cleanmachine.tmp";
        try
        {
            using var directoryLease = OperatingSystem.IsWindows() ? SafeDeletion.Pin(Path.GetDirectoryName(path)!) : null;
            var lines = ReadPreferenceText(path).Split('\n');
            var filtered = lines
                .Where(line => !IsDownloadDirectoryPreference(line))
                .ToArray();
            if (filtered.Length == lines.Length) return false;

            var backup = path + ".cleanmachine.bak";
            if (File.Exists(backup) && NativeSafety.IsReparsePoint(backup))
                throw new IOException("Preference backup is a reparse point.");
            if (File.Exists(backup)) SafeDeletion.DeleteFile(backup, Path.GetDirectoryName(path)!);
            File.Copy(path, backup, overwrite: false);
            // Temp-then-swap so a crash mid-write can never truncate prefs.js.
            File.WriteAllLines(temp, filtered);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped.Add(new CleanupIssue(path, ex.Message));
            return false;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
