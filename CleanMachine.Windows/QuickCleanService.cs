namespace CleanMachine.Windows;

/// <summary>The four Overview areas that expose a one-click "Quick Clean".</summary>
public enum QuickCleanArea { Browsers, Windows, Registry, Apps }

/// <summary>Result of one area's Quick Clean. <see cref="Note"/> carries a short
/// explanation when nothing was cleaned (e.g. no items selected).
/// <para><see cref="Examined"/> is how many cleanup candidates the run actually
/// looked at. It separates "the user configured nothing, so nothing happened"
/// (0) from "the run inspected real candidates and removed none" (&gt; 0) -
/// see <see cref="ShouldRecordActivity"/>. Defaults to 0 so every existing
/// construction site keeps compiling.</para></summary>
public sealed record QuickCleanResult(int Items, long Bytes, IReadOnlyList<string> Issues, string? Note = null, IReadOnlyList<string>? Details = null, int Examined = 0);

/// <summary>Runs a single Overview area's Quick Clean using the user's saved per-area
/// item selection (<see cref="AppSettings.QuickCleanWindowsCategories"/> and friends).
/// Only safe, non-destructive items are ever cleaned: browser caches, Safe-risk
/// Windows categories, application temp files, and registry findings that pass the
/// safety gate (backed up first). The one exception is the Windows Recycle Bin,
/// offered as an explicit opt-in in the Quick Clean picker; ticking it there is
/// the confirmation required for its Review risk. A run that cleaned something
/// records one stats + activity entry; a run that examined candidates but
/// removed nothing still records an activity entry explaining why (see
/// <see cref="ShouldRecordActivity"/>), because a Quick Clean that quietly
/// writes nothing is indistinguishable from one that never ran.</summary>
public static class QuickCleanService
{
    /// <summary>The Windows Cleanup catalog id for the Recycle Bin, offered in the
    /// Quick Clean picker as an explicit opt-in despite its Review risk.</summary>
    internal const string RecycleBinCategoryId = "system-recycle-bin";

    /// <summary>Registry Care categories eligible for Quick Clean (see RegistryFinding.Category).</summary>
    public static readonly IReadOnlyList<string> RegistryCategories =
        ["Installer/Uninstaller", "File Extensions", "MUI Cache", "Windows Startup", "Sound AppEvents", "Shell Cache", "App Paths", "Open With", "Compatibility Assistant"];

    public static string Title(QuickCleanArea area) => area switch
    {
        QuickCleanArea.Browsers => "Browser Caches",
        QuickCleanArea.Windows => "Windows Cleanup",
        QuickCleanArea.Registry => "Registry Care",
        _ => "Application Temp Files"
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
                $"Quick Clean - {Title(area)}",
                CleanedDetail(result),
                result.Details));
        }
        else if (ShouldRecordActivity(result))
        {
            // Examined candidates but reclaimed nothing. No stats entry (nothing
            // was reclaimed), but the activity log must say the run happened and
            // why it came back empty - otherwise a zero-item Registry Care Quick
            // Clean looks exactly like a Quick Clean that never ran.
            await new ActivityStore().AddAsync(new ActivityEntry(
                DateTimeOffset.UtcNow,
                $"Quick Clean - {Title(area)}",
                NothingRemovedDetail(result)));
        }
        return result;
    }

    /// <summary>Whether a run that removed nothing still belongs in the activity
    /// log. True when it examined real candidates (<see cref="QuickCleanResult.Examined"/>
    /// &gt; 0); false when the user simply configured nothing for this area, which
    /// is a no-op rather than an event.</summary>
    internal static bool ShouldRecordActivity(QuickCleanResult result)
        => result.Items == 0 && result.Bytes == 0 && result.Examined > 0;

    /// <summary>Detail line for a run that reclaimed something.</summary>
    internal static string CleanedDetail(QuickCleanResult result)
        => $"Cleaned {result.Items:N0} item(s), {AppNotifications.FormatBytes(result.Bytes)} recovered"
            + (result.Issues.Count > 0 ? $" - {result.Issues.Count} skipped" : string.Empty);

    /// <summary>The one-line outcome shown under the Overview card's Quick Clean
    /// button. Shares the <see cref="NothingRemovedDetail"/> wording with the
    /// activity entry so the card and the log can never disagree about what a
    /// zero-item run did.</summary>
    internal static string SummaryText(QuickCleanResult result)
    {
        if (result.Items == 0 && result.Bytes == 0)
        {
            if (!string.IsNullOrWhiteSpace(result.Note)) return result.Note;
            if (ShouldRecordActivity(result)) return NothingRemovedDetail(result) + ".";
            return $"0 item(s) removed, {AppNotifications.FormatBytes(result.Bytes)} recovered";
        }
        return $"{result.Items:N0} item(s) removed, {AppNotifications.FormatBytes(result.Bytes)} recovered"
            + (result.Issues.Count > 0 ? $" - {result.Issues.Count} skipped." : ".");
    }

    /// <summary>Detail line for a run that examined candidates and removed none.
    /// Always names the candidate count, then the skip count, then the area's own
    /// reason (<see cref="QuickCleanResult.Note"/>) when it has one - so the entry
    /// explains itself instead of leaving the user guessing.</summary>
    internal static string NothingRemovedDetail(QuickCleanResult result)
    {
        var parts = new List<string> { $"Nothing removed from {result.Examined:N0} candidate(s)" };
        if (result.Issues.Count > 0) parts.Add($"{result.Issues.Count} skipped");
        if (!string.IsNullOrWhiteSpace(result.Note)) parts.Add(result.Note);
        return string.Join(" - ", parts);
    }

    private static async Task<QuickCleanResult> RunBrowsersAsync(AppSettings settings, CancellationToken token)
    {
        if (settings.QuickCleanBrowsers.Count == 0)
            return new QuickCleanResult(0, 0, [], "No browsers selected.");
        var selection = BrowserCacheSelection(settings.QuickCleanBrowsers);
        if (selection.Count == 0)
            return new QuickCleanResult(0, 0, [], "None of the selected browsers is installed.");
        // Caches are safe to clear even with a browser open (locked and recently
        // written files are skipped), so Quick Clean does not require browsers to
        // be closed.
        var report = await new BrowserCleanupService().CleanItemsAsync(
            selection, token, requireBrowsersClosed: false,
            excludedPaths: settings.ExcludedPaths,
            skipModifiedWithin: BrowserCacheRecentWindow);
        return new QuickCleanResult(report.Result.ItemsRemoved, report.Result.BytesRecovered, Summarize(report.Skipped),
            Examined: selection.Count);
    }

    /// <summary>Cache files written this recently are left alone by the cache-only
    /// browser cleans that run while a browser may still be open.</summary>
    internal static readonly TimeSpan BrowserCacheRecentWindow = TimeSpan.FromMinutes(10);

    /// <summary>The (browser, "cache") pairs for every installed catalog browser in
    /// <paramref name="browserIds"/>. Only the non-destructive cache item is ever
    /// chosen here: this backs Quick Clean and scheduled runs, which never delete
    /// history, cookies or other user data.</summary>
    internal static IReadOnlyList<(string BrowserId, string ItemId)> BrowserCacheSelection(IEnumerable<string> browserIds)
        => browserIds
            .Select(BrowserCatalog.Find)
            .OfType<BrowserDefinition>()
            .Where(b => b.Family != BrowserFamily.InternetExplorer && BrowserCatalog.IsInstalled(b))
            .DistinctBy(b => b.Id)
            .Select(b => (BrowserId: b.Id, ItemId: "cache"))
            .ToList();

    private static async Task<QuickCleanResult> RunWindowsAsync(AppSettings settings, CancellationToken token)
    {
        // Safe categories per the user's selection (or the Windows Cleanup page's
        // enabled set when unconfigured), plus the Recycle Bin as an explicit opt-in:
        // IsWindowsSelected only ever selects the bin when the user ticked it in the
        // Quick Clean picker, and ticking it there is the confirmation its Review risk
        // requires (the same way the schedule editor opts into Review categories).
        var categories = WindowsCleanupService.Catalog
            .Where(c => (c.Risk == CleanupRisk.Safe || c.Id == RecycleBinCategoryId)
                        && IsWindowsSelected(c, settings))
            .ToList();
        if (categories.Count == 0) return new QuickCleanResult(0, 0, [], "No categories selected.");
        var report = await new WindowsCleanupService().CleanSelectedAsync(
            categories,
            new WindowsCleanupOptions(
                // Ticking the Recycle Bin in the picker is the explicit confirmation;
                // without this flag the service refuses every Review-risk category.
                ConfirmReviewCategories: categories.Any(c => c.Risk != CleanupRisk.Safe),
                ExcludedPaths: settings.ExcludedPaths),
            cancellationToken: token);
        return new QuickCleanResult(report.Result.ItemsRemoved, report.Result.BytesRecovered, Summarize(report.Skipped),
            Details: ActivityStore.BreakdownLines(report.Breakdown),
            // Windows categories are cleaned wholesale and the service reports no
            // candidate count, so there is no "examined" number to log here.
            Examined: 0);
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
        // PrepareReviewAsync drops anything failing the safety gate (LowRisk and
        // confidence >= 70). Say so explicitly: without a note the caller fell back
        // to a bare "0 item(s) removed" and the run looked like it never happened.
        if (review.Findings.Count == 0)
            return new QuickCleanResult(0, 0, [],
                $"No registry findings were eligible: all {selected.Length:N0} failed the safety gate.",
                Examined: selected.Length);
        // Same rule as the Registry Care page: never clean the registry without a backup.
        if (review.Findings.Count > 0 && review.Backups.Count == 0)
            return new QuickCleanResult(0, 0,
                [string.IsNullOrWhiteSpace(review.BackupFailure)
                    ? "Skipped: no registry backup could be created."
                    : $"Skipped: {review.BackupFailure}"],
                "Registry cleanup was refused: no restore point.",
                Examined: review.Findings.Count);
        var clean = await service.CleanAsync(review, token);
        return new QuickCleanResult(clean.Removed, 0, Summarize(clean.Skipped),
            clean.Removed == 0 ? "No registry values were removed." : null,
            RegistryDetailLines(clean.Cleaned),
            review.Findings.Count);
    }

    /// <summary>Per-category drill-down for a Registry Quick Clean entry, matching
    /// the shape <see cref="ActivityStore.BreakdownLines"/> produces for the other
    /// areas (largest contributor first, registry values carry no byte count).</summary>
    internal static IReadOnlyList<string>? RegistryDetailLines(IReadOnlyList<RegistryFinding>? cleaned)
        => cleaned is { Count: > 0 }
            ? cleaned
                .GroupBy(f => f.Category, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => $"{g.Key} - {g.Count():N0} item(s)")
                .ToList()
            : null;

    private static async Task<QuickCleanResult> RunAppsAsync(AppSettings settings, CancellationToken token)
    {
        var service = new AppCleanupService();
        var scans = await service.ScanAllAsync(token);
        var ids = settings.QuickCleanApps; // null = every installed app
        var selection = AppCleanupService.AllItems(scans.Where(s => ids is null || ids.Contains(s.Id)));
        if (selection.Count == 0) return new QuickCleanResult(0, 0, [], "Nothing to clean.");
        var report = await service.CleanAsync(selection, token, excludedPaths: settings.ExcludedPaths);
        return new QuickCleanResult(report.Result.ItemsRemoved, report.Result.BytesRecovered, Summarize(report.Skipped),
            Examined: selection.Count);
    }

    /// <summary>Whether a Windows category is included in Quick Clean: the user's
    /// explicit selection when configured (this is also how the Review-risk Recycle
    /// Bin is opted in - only a tick in the Quick Clean picker selects it), otherwise
    /// the Safe categories enabled on the Windows Cleanup page (Review categories,
    /// including the Recycle Bin, stay out of the unconfigured fallback).</summary>
    internal static bool IsWindowsSelected(CleanupCategory category, AppSettings settings)
        => settings.QuickCleanWindowsCategories is { } set
            ? set.Contains(category.Id)
            : category.Risk == CleanupRisk.Safe && WindowsCleanupService.IsEnabled(category, settings);

    private static IReadOnlyList<string> Summarize(IReadOnlyList<CleanupIssue> skipped)
        => skipped.Take(20).Select(i => $"{i.Path}: {i.Reason}").ToList();
}
