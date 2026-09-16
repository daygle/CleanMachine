namespace CleanMachine.Windows;

/// <summary>The four Overview areas that expose a one-click "Quick Clean".</summary>
public enum QuickCleanArea { Browsers, Windows, Registry, Apps }

/// <summary>Result of one area's Quick Clean. <see cref="Note"/> carries a short
/// explanation when nothing was cleaned (e.g. no items selected).</summary>
public sealed record QuickCleanResult(int Items, long Bytes, IReadOnlyList<string> Issues, string? Note = null);

/// <summary>Runs a single Overview area's Quick Clean using the user's saved per-area
/// item selection (<see cref="AppSettings.QuickCleanWindowsCategories"/> and friends).
/// Only safe, non-destructive items are ever cleaned: browser caches, Safe-risk
/// Windows categories, application temp files, and registry findings that pass the
/// safety gate (backed up first). Each run records one stats + activity entry.</summary>
public static class QuickCleanService
{
    /// <summary>Registry Care categories eligible for Quick Clean (see RegistryFinding.Category).</summary>
    public static readonly IReadOnlyList<string> RegistryCategories =
        ["Installer/Uninstaller", "File Extensions", "MUI Cache", "Windows Startup", "Sound AppEvents", "Shell Cache", "App Paths"];

    public static string Title(QuickCleanArea area) => area switch
    {
        QuickCleanArea.Browsers => "Browser caches",
        QuickCleanArea.Windows => "Windows cleanup",
        QuickCleanArea.Registry => "Registry care",
        _ => "Application temp files"
    };

    public static async Task<QuickCleanResult> RunAsync(QuickCleanArea area, CancellationToken token = default)
    {
        var settings = await AppSettings.LoadAsync(token);
        var result = area switch
        {
            QuickCleanArea.Browsers => await RunBrowsersAsync(settings, token),
            QuickCleanArea.Windows => await RunWindowsAsync(settings, token),
            QuickCleanArea.Registry => await RunRegistryAsync(settings, token),
            _ => await RunAppsAsync(settings, token)
        };

        if (result.Items > 0 || result.Bytes > 0)
        {
            // Fresh token so the record survives even if the caller's token is cancelled.
            await new CleanupStatsStore().RecordAsync(result.Items, result.Bytes, CancellationToken.None);
            await new ActivityStore().AddAsync(new ActivityEntry(
                DateTimeOffset.UtcNow,
                $"Quick Clean · {Title(area)}",
                $"Cleaned {result.Items:N0} item(s), {AppNotifications.FormatBytes(result.Bytes)} recovered"
                + (result.Issues.Count > 0 ? $" · {result.Issues.Count} skipped" : string.Empty)));
        }
        return result;
    }

    private static async Task<QuickCleanResult> RunBrowsersAsync(AppSettings settings, CancellationToken token)
    {
        if (settings.QuickCleanBrowsers.Count == 0)
            return new QuickCleanResult(0, 0, [], "No browsers selected.");
        var service = new BrowserCleanupService();
        var targets = await service.ScanAsync(settings.QuickCleanBrowsers, excludedPaths: settings.ExcludedPaths, token: token);
        // Caches are safe to clear even with a browser open (locked files are skipped),
        // so Quick Clean does not require browsers to be closed.
        var report = await service.CleanWithReportAsync(
            targets, new BrowserCleanupOptions(settings.ExcludedPaths, RequireBrowsersClosed: false), token: token);
        return new QuickCleanResult(report.Result.ItemsRemoved, report.Result.BytesRecovered, Summarize(report.Skipped));
    }

    private static async Task<QuickCleanResult> RunWindowsAsync(AppSettings settings, CancellationToken token)
    {
        var categories = WindowsCleanupService.Catalog
            .Where(c => c.Risk == CleanupRisk.Safe && IsWindowsSelected(c, settings))
            .ToList();
        if (categories.Count == 0) return new QuickCleanResult(0, 0, [], "No categories selected.");
        var report = await new WindowsCleanupService().CleanSelectedAsync(
            categories,
            new WindowsCleanupOptions(ConfirmReviewCategories: false, AllowElevation: false, ExcludedPaths: settings.ExcludedPaths),
            cancellationToken: token);
        return new QuickCleanResult(report.Result.ItemsRemoved, report.Result.BytesRecovered, Summarize(report.Skipped));
    }

    private static async Task<QuickCleanResult> RunRegistryAsync(AppSettings settings, CancellationToken token)
    {
        var categories = settings.QuickCleanRegistryCategories ?? RegistryCategories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (categories.Count == 0) return new QuickCleanResult(0, 0, [], "No categories selected.");
        var service = new RegistryCareService();
        var scan = await service.ScanAsync(token);
        var selected = scan.Findings.Where(f => categories.Contains(f.Category)).ToArray();
        if (selected.Length == 0) return new QuickCleanResult(0, 0, [], "Nothing to clean.");
        var review = await service.PrepareReviewAsync(selected, token);
        // Same rule as the Registry Care page: never clean the registry without a backup.
        if (review.Findings.Count > 0 && review.Backups.Count == 0)
            return new QuickCleanResult(0, 0, ["Skipped: no registry backup could be created."]);
        var clean = await service.CleanAsync(review, token);
        return new QuickCleanResult(clean.Removed, 0, Summarize(clean.Skipped));
    }

    private static async Task<QuickCleanResult> RunAppsAsync(AppSettings settings, CancellationToken token)
    {
        var scans = await new AppCleanupService().ScanAllAsync(token);
        var ids = settings.QuickCleanApps; // null = every installed app
        var selection = scans
            .Where(s => s.Installed && s.Items.Count > 0 && (ids is null || ids.Contains(s.Id)))
            .SelectMany(s => s.Items.Select((_, index) => (s.Id, index)))
            .ToList();
        if (selection.Count == 0) return new QuickCleanResult(0, 0, [], "Nothing to clean.");
        var report = await new AppCleanupService().CleanAsync(selection, secureDelete: null, token);
        return new QuickCleanResult(report.Result.ItemsRemoved, report.Result.BytesRecovered, Summarize(report.Skipped));
    }

    /// <summary>Whether a Safe Windows category is included in Quick Clean: the user's
    /// explicit selection when configured, otherwise the categories enabled on the
    /// Windows Cleanup page.</summary>
    internal static bool IsWindowsSelected(CleanupCategory category, AppSettings settings)
        => settings.QuickCleanWindowsCategories is { } set
            ? set.Contains(category.Id)
            : WindowsCleanupService.IsEnabled(category, settings);

    private static IReadOnlyList<string> Summarize(IReadOnlyList<CleanupIssue> skipped)
        => skipped.Take(20).Select(i => $"{i.Path}: {i.Reason}").ToList();
}
