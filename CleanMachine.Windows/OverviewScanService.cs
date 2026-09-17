namespace CleanMachine.Windows;

/// <summary>One area's "could be cleaned right now" figure for the Overview cards.</summary>
public sealed record AreaSummary(string Card);

public sealed record OverviewScanResult(AreaSummary Browsers, AreaSummary Windows, AreaSummary Registry, AreaSummary Apps);

/// <summary>Computes lightweight, read-only availability summaries for each cleanup
/// area (browsers, Windows categories, registry care, application temp files).
/// Nothing is removed here - the numbers mirror what the respective pages would
/// offer to clean, respecting the user's enabled categories and exclusions.
/// Results are cached for a few minutes so revisiting Overview is instant; any
/// cleanup that records stats invalidates the cache automatically.</summary>
public sealed class OverviewScanService
{
    /// <summary>How long a scan result stays fresh.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private static readonly object Gate = new();
    private static OverviewScanResult? _cached;
    private static DateTimeOffset _cachedAt;
    private static Task<OverviewScanResult>? _inFlight;
    private static int _generation;
    private static IReadOnlyList<CleanupItem>? _windowsItems;
    private static DateTimeOffset _windowsItemsAt;
    private static Task<IReadOnlyList<CleanupItem>?>? _windowsItemsTask;
    private static int _windowsItemsGeneration;

    /// <summary>Drops the cached summaries so the next scan recomputes everything.
    /// Also orphans any in-flight scan: its (now stale) result will not be cached.</summary>
    public static void InvalidateCache()
    {
        lock (Gate)
        {
            _cached = null;
            _cachedAt = default;
            _generation++;
            // Any scan already running is now stale; the next call starts fresh.
            // Its result is discarded on completion (generation mismatch below).
            _inFlight = null;
            _windowsItems = null;
            _windowsItemsTask = null;
            _windowsItemsAt = default;
        }
    }

    /// <summary>Scans every area read-only and fills the availability cards with
    /// what could be cleaned right now. Each scan already returns a ready-to-display
    /// card string; failures degrade that card to "Scan failed".</summary>
    public static async Task<OverviewScanResult> ScanAllAsync(
        CancellationToken token = default, bool forceRefresh = false)
    {
        if (forceRefresh) InvalidateCache();

        Task<OverviewScanResult> scan;
        int generation;
        lock (Gate)
        {
            var now = DateTimeOffset.UtcNow;
            generation = _generation;
            if (_cached is not null && now - _cachedAt < CacheLifetime) return _cached;
            // Reuse (and share) an already-running scan instead of starting another.
            _inFlight ??= ScanAllUncachedAsync();
            scan = _inFlight;
        }

        var result = await scan.WaitAsync(token);

        lock (Gate)
        {
            // This scan finished, so it is no longer the in-flight one. If the cache
            // was invalidated meanwhile (generation bumped), the result is stale:
            // return it but do not cache it, so the next call scans fresh.
            if (ReferenceEquals(_inFlight, scan)) _inFlight = null;
            if (generation == _generation)
            {
                _cached = result;
                _cachedAt = DateTimeOffset.UtcNow;
            }
        }
        return result;
    }

    /// <summary>Runs the four area scans concurrently. Each already completes with a
    /// card string (never throws), so the aggregate is simply their combination.</summary>
    private static async Task<OverviewScanResult> ScanAllUncachedAsync()
    {
        // All four scans are independent and I/O bound; run them concurrently so
        // the Overview cards fill at the speed of the slowest one, not the sum.
        var browsers = ScanBrowsersAsync();
        var windows = ScanWindowsAsync();
        var registry = ScanRegistryAsync();
        var apps = ScanAppsAsync();
        await Task.WhenAll(browsers, windows, registry, apps);
        return new OverviewScanResult(browsers.Result, windows.Result, registry.Result, apps.Result);
    }

    private static async Task<AreaSummary> ScanBrowsersAsync()
    {
        try
        {
            var scans = await new BrowserCleanupService().DetectAndScanAsync();
            var installed = scans.Where(s => s.Installed).ToList();
            var items = installed.SelectMany(s => s.Items).Where(i => i.Bytes > 0).ToList();
            if (items.Count == 0)
                return new AreaSummary(installed.Count == 0 ? "No supported browsers detected" : "Clean");
            return new AreaSummary(
                $"{AppNotifications.FormatBytes(items.Sum(i => i.Bytes))} could be freed - " +
                $"{items.Select(i => i.Id).Distinct().Count()} item(s) across {installed.Count(s => s.Items.Any(i => i.Bytes > 0))} browser(s)");
        }
        catch { return new AreaSummary("Scan failed"); }
    }

    /// <summary>Scans every Windows Cleanup category, respecting the user's
    /// exclusions, and caches the raw result alongside the area summaries (so the
    /// Windows Cleanup page and the Overview card share one scan). Returns null on
    /// failure; callers degrade gracefully.</summary>
    public static async Task<IReadOnlyList<CleanupItem>?> ScanWindowsItemsAsync()
    {
        Task<IReadOnlyList<CleanupItem>?> scan;
        lock (Gate)
        {
            if (_windowsItems is not null && DateTimeOffset.UtcNow - _windowsItemsAt < CacheLifetime)
                return _windowsItems;
            if (_windowsItemsTask is null)
            {
                _windowsItemsGeneration = _generation;
                _windowsItemsTask = Task.Run(async () =>
                {
                    try
                    {
                        var settings = await AppSettings.LoadAsync();
                        return (IReadOnlyList<CleanupItem>?)await Task.Run(
                            () => new WindowsCleanupService().Scan(settings.ExcludedPaths));
                    }
                    catch { return null; }
                });
            }
            scan = _windowsItemsTask;
        }

        var result = await scan;

        lock (Gate)
        {
            // Clear the slot whether we succeeded or failed: a failed scan must not
            // stick (the next call retries). If the cache was invalidated while the
            // scan ran, the result is stale - discard it instead of caching it.
            _windowsItemsTask = null;
            if (result is not null && _windowsItemsGeneration == _generation)
            {
                _windowsItems = result;
                _windowsItemsAt = DateTimeOffset.UtcNow;
            }
        }
        return result;
    }

    private static async Task<AreaSummary> ScanWindowsAsync()
    {
        try
        {
            var items = await ScanWindowsItemsAsync();
            if (items is null) return new AreaSummary("Scan failed");
            var settings = await AppSettings.LoadAsync();
            var enabledIds = WindowsCleanupService.Catalog
                .Where(c => WindowsCleanupService.IsEnabled(c, settings))
                .Select(c => c.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var mine = items.Where(i => enabledIds.Contains(i.Category.Id)).ToList();
            var bytes = mine.Where(i => i.Category.Kind != CleanupKind.RegistryValues).Sum(i => i.Bytes);
            // For registry-kind categories the service stores the entry count in Bytes.
            var entries = mine.Where(i => i.Category.Kind == CleanupKind.RegistryValues).Sum(i => i.Bytes);
            var categoriesWithData = mine.Count(i => i.Bytes > 0);

            if (bytes == 0 && entries == 0) return new AreaSummary("Clean");

            var detail = $"{categoriesWithData} categories with data";
            var headline = bytes > 0 ? $"{AppNotifications.FormatBytes(bytes)} could be freed" : $"{entries:N0} history entries";
            if (bytes > 0 && entries > 0) detail += $" - {entries:N0} history entries";
            return new AreaSummary($"{headline} - {detail}");
        }
        catch { return new AreaSummary("Scan failed"); }
    }

    private static async Task<AreaSummary> ScanRegistryAsync()
    {
        try
        {
            var review = await Task.Run(() => new RegistryCareService().ScanAsync());
            return review.Findings.Count > 0
                ? new AreaSummary($"{review.Findings.Count:N0} review item(s) found")
                : new AreaSummary("Clean");
        }
        catch { return new AreaSummary("Scan failed"); }
    }

    private static async Task<AreaSummary> ScanAppsAsync()
    {
        try
        {
            var scans = await new AppCleanupService().ScanAllAsync();
            var withItems = scans.Where(s => s.Installed && s.Items.Count > 0).ToList();
            if (withItems.Count == 0) return new AreaSummary("Clean");
            return new AreaSummary(
                $"{AppNotifications.FormatBytes(withItems.Sum(s => s.Items.Sum(i => i.Bytes)))} could be freed - " +
                $"{withItems.Count} app(s) with temp files");
        }
        catch { return new AreaSummary("Scan failed"); }
    }
}
