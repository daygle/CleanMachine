namespace CleanMachine.Windows;

/// <summary>One area's contribution to the Clean All plan, for the confirmation UI.
/// <see cref="Note"/> areas (e.g. a skipped area) carry no bytes.</summary>
public sealed record CleanAllPlanArea(string Name, string Detail, long Bytes);

/// <summary>Everything 'Clean All Safe Items' would clean, gathered read-only before
/// the single confirmation. Only safe items are ever included: non-destructive
/// browser caches, enabled Safe-risk Windows categories, application temp files and
/// registry findings that pass the safety gate.</summary>
public sealed record CleanAllPlan(
    IReadOnlyList<(string BrowserId, string ItemId)> BrowserSelection,
    IReadOnlyList<CleanupCategory> WindowsCategories,
    IReadOnlyList<(string AppId, int ItemIndex)> AppSelection,
    IReadOnlyList<RegistryFinding> RegistryFindings,
    IReadOnlyList<CleanAllPlanArea> Areas,
    long TotalBytes,
    int TotalItems)
{
    public bool HasWork => BrowserSelection.Count > 0 || WindowsCategories.Count > 0
        || AppSelection.Count > 0 || RegistryFindings.Count > 0;
}

public sealed record CleanAllOutcome(int ItemsRemoved, long BytesRecovered, IReadOnlyList<string> Issues);

/// <summary>Runs every area's safe cleanables in sequence behind one confirmation.
/// Review/Advanced-risk Windows categories, destructive browser items, downloads and
/// user documents are never touched here; registry findings are backed up first and
/// only those passing the existing safety gate are deleted.</summary>
public sealed class CleanAllService
{
    /// <summary>Gathers, read-only, what each area could clean right now. Mirrors the
    /// numbers the individual pages and the Overview availability cards show.</summary>
    public static async Task<CleanAllPlan> BuildPlanAsync(CancellationToken token = default)
    {
        var settings = await AppSettings.LoadAsync(token);
        var areas = new List<CleanAllPlanArea>();
        long totalBytes = 0;
        int totalItems = 0;

        // ---- Browsers: non-destructive cache items with data. Skipped entirely when
        // a browser is running (its cache files would be locked anyway).
        var browserSelection = new List<(string BrowserId, string ItemId)>();
        var running = BrowserCleanupService.GetRunningBrowsers();
        if (running.Count > 0)
        {
            var names = running.Select(PrettyBrowser);
            areas.Add(new CleanAllPlanArea("Browser caches", $"Skipped - {string.Join(", ", names)} open", 0));
        }
        else
        {
            var scans = await new BrowserCleanupService().DetectAndScanAsync(token);
            var withData = scans.Where(s => s.Installed).ToList();
            foreach (var scan in withData)
                foreach (var item in scan.Items.Where(i => !i.Destructive && i.Bytes > 0))
                    browserSelection.Add((scan.Id, item.Id));

            var browserBytes = withData.SelectMany(s => s.Items).Where(i => !i.Destructive).Sum(i => i.Bytes);
            var browserFiles = withData.SelectMany(s => s.Items).Where(i => !i.Destructive).Sum(i => i.FileCount);
            if (browserSelection.Count > 0)
            {
                var browserCount = withData.Count(s => s.Items.Any(i => !i.Destructive && i.Bytes > 0));
                areas.Add(new CleanAllPlanArea("Browser caches",
                    $"{AppNotifications.FormatBytes(browserBytes)} · {browserFiles:N0} file(s) across {browserCount} browser(s)", browserBytes));
                totalBytes += browserBytes;
                totalItems += browserFiles;
            }
        }

        // ---- Windows: enabled Safe-risk categories with data.
        var enabledIds = WindowsCleanupService.Catalog
            .Where(c => WindowsCleanupService.IsEnabled(c, settings))
            .Select(c => c.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // The catalog scan is synchronous and can enumerate many files: keep it off the UI thread.
        var scanned = await Task.Run(() => new WindowsCleanupService().Scan(settings.ExcludedPaths), token);
        var windowsCategories = scanned
            .Where(i => i.Category.Risk == CleanupRisk.Safe && enabledIds.Contains(i.Category.Id) && i.Bytes > 0)
            .Select(i => i.Category)
            .ToList();
        if (windowsCategories.Count > 0)
        {
            var mine = scanned.Where(i => windowsCategories.Contains(i.Category)).ToList();
            var windowsBytes = mine.Where(i => i.Category.Kind != CleanupKind.RegistryValues).Sum(i => i.Bytes);
            // For registry-kind categories the service stores the value count in Bytes.
            var windowsEntries = mine.Where(i => i.Category.Kind == CleanupKind.RegistryValues).Sum(i => (int)i.Bytes);
            var detail = windowsBytes > 0
                ? $"{AppNotifications.FormatBytes(windowsBytes)} · {windowsCategories.Count} safe category(ies) with data"
                : $"{windowsCategories.Count} safe category(ies) with data";
            if (windowsEntries > 0) detail += $" · {windowsEntries:N0} history entries";
            areas.Add(new CleanAllPlanArea("Windows cleanup", detail, windowsBytes));
            totalBytes += windowsBytes;
            totalItems += windowsEntries;
        }

        // ---- Application temp files: every detected app's temp items.
        var appScans = await new AppCleanupService().ScanAllAsync(token);
        var appSelection = new List<(string AppId, int ItemIndex)>();
        foreach (var scan in appScans.Where(s => s.Installed && s.Items.Count > 0))
            for (var i = 0; i < scan.Items.Count; i++)
                appSelection.Add((scan.Id, i));
        if (appSelection.Count > 0)
        {
            var appBytes = appScans.Where(s => s.Installed).SelectMany(s => s.Items).Sum(i => i.Bytes);
            var appFiles = appScans.Where(s => s.Installed).SelectMany(s => s.Items).Sum(i => i.FileCount);
            var appCount = appScans.Count(s => s.Installed && s.Items.Count > 0);
            areas.Add(new CleanAllPlanArea("Application temp files",
                $"{AppNotifications.FormatBytes(appBytes)} · {appFiles:N0} file(s) across {appCount} app(s)", appBytes));
            totalBytes += appBytes;
            totalItems += appFiles;
        }

        // ---- Registry: only findings that pass the existing safety gate.
        var registry = new RegistryCareService();
        var review = await Task.Run(() => registry.ScanAsync(token), token);
        var registryFindings = review.Findings.Where(RegistryCareService.IsCleanable).ToList();
        if (registryFindings.Count > 0)
        {
            areas.Add(new CleanAllPlanArea("Registry care",
                $"{registryFindings.Count} item(s), backed up first", 0));
            totalItems += registryFindings.Count;
        }

        return new CleanAllPlan(browserSelection, windowsCategories, appSelection, registryFindings, areas, totalBytes, totalItems);
    }

    /// <summary>Runs the plan's areas in sequence. Each area's failures are collected
    /// rather than thrown, so one locked cache never blocks the rest of the run.</summary>
    public static async Task<CleanAllOutcome> RunAsync(
        CleanAllPlan plan, IProgress<string>? progress = null, CancellationToken token = default)
    {
        var issues = new List<string>();
        var items = 0;
        long bytes = 0;
        var settings = await AppSettings.LoadAsync(token);

        if (plan.BrowserSelection.Count > 0)
        {
            progress?.Report("Cleaning browser caches…");
            try
            {
                var report = await Task.Run(
                    () => new BrowserCleanupService().CleanItemsAsync(plan.BrowserSelection, secureDelete: null, token), token);
                items += report.Result.ItemsRemoved;
                bytes += report.Result.BytesRecovered;
                AddIssues(issues, report.Skipped);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                issues.Add($"Browser caches: {ex.Message}");
            }
        }

        if (plan.WindowsCategories.Count > 0)
        {
            progress?.Report("Cleaning Windows safe categories…");
            try
            {
                var phase = new Progress<CleanupProgress>(p =>
                    progress?.Report(p.Total > 0 ? $"Cleaning {p.Phase} ({p.Completed}/{p.Total})…" : $"Cleaning {p.Phase}…"));
                var report = await new WindowsCleanupService().CleanSelectedAsync(
                    plan.WindowsCategories,
                    new WindowsCleanupOptions(ConfirmReviewCategories: false, AllowElevation: false, ExcludedPaths: settings.ExcludedPaths),
                    phase, token);
                items += report.Result.ItemsRemoved;
                bytes += report.Result.BytesRecovered;
                AddIssues(issues, report.Skipped);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                issues.Add($"Windows cleanup: {ex.Message}");
            }
        }

        if (plan.AppSelection.Count > 0)
        {
            progress?.Report("Cleaning application temp files…");
            try
            {
                var report = await Task.Run(
                    () => new AppCleanupService().CleanAsync(plan.AppSelection, secureDelete: null, token), token);
                items += report.Result.ItemsRemoved;
                bytes += report.Result.BytesRecovered;
                AddIssues(issues, report.Skipped);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                issues.Add($"Application cleanup: {ex.Message}");
            }
        }

        if (plan.RegistryFindings.Count > 0)
        {
            progress?.Report("Backing up and cleaning the registry…");
            try
            {
                var registry = new RegistryCareService();
                var review = await Task.Run(() => registry.PrepareReviewAsync(plan.RegistryFindings, token), token);
                // Same rule as the manual page: never clean the registry without a backup.
                if (review.Findings.Count > 0 && review.Backups.Count == 0)
                    issues.Add("Registry skipped: no backup could be created.");
                else
                {
                    var result = await Task.Run(() => registry.CleanAsync(review, token), token);
                    items += result.Removed;
                    AddIssues(issues, result.Skipped);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                issues.Add($"Registry care: {ex.Message}");
            }
        }

        // One stats entry and one activity entry for the whole run. Recorded with a
        // fresh token so a cancelled run still keeps its partial results.
        _ = new CleanupStatsStore().RecordAsync(items, bytes);
        await new ActivityStore().AddAsync(new ActivityEntry(
            DateTimeOffset.UtcNow,
            "Clean All",
            $"Cleaned {items:N0} item(s), {AppNotifications.FormatBytes(bytes)} recovered" +
            (issues.Count > 0 ? $" · {issues.Count} skipped" : string.Empty)));

        return new CleanAllOutcome(items, bytes, issues);
    }

    /// <summary>Keeps the issue list readable: at most 20 individual skipped files,
    /// then one summary line. Safe to call across multiple areas.</summary>
    private static void AddIssues(List<string> issues, IReadOnlyList<CleanupIssue> skipped)
    {
        if (issues.Count > 20) return; // summary line already present
        foreach (var issue in skipped)
        {
            if (issues.Count == 20)
            {
                issues.Add("…more skipped file(s) not listed");
                return;
            }
            issues.Add($"{issue.Path}: {issue.Reason}");
        }
    }

    private static string PrettyBrowser(string processName) => processName.ToLowerInvariant() switch
    {
        "msedge" => "Edge",
        _ => char.ToUpperInvariant(processName[0]) + processName[1..]
    };
}
