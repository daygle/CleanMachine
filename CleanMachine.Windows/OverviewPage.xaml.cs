using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CleanMachine.Windows;

public sealed partial class OverviewPage : Page
{
    public OverviewPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => { await LoadStatsAsync(); await LoadAvailabilityAsync(); };
    }

    /// <summary>Scans every area read-only and fills the availability cards with
    /// what could be cleaned right now. Results are cached for a few minutes so
    /// revisiting the page is instant; pass forceRefresh after a clean so the
    /// cards never show pre-clean numbers. Each scan already returns a
    /// ready-to-display card string; failures degrade that card to "Scan failed".</summary>
    private async Task LoadAvailabilityAsync(bool forceRefresh = false)
    {
        var result = await OverviewScanService.ScanAllAsync(forceRefresh: forceRefresh);
        BrowsersSummary.Text = result.Browsers.Card;
        WindowsSummary.Text = result.Windows.Card;
        RegistrySummary.Text = result.Registry.Card;
        AppsSummary.Text = result.Apps.Card;
    }

    private async Task LoadStatsAsync()
    {
        try
        {
            var stats = await new CleanupStatsStore().LoadAsync();
            // Derive the recent window from the stats just loaded instead of reading
            // stats.json a second time.
            var (recentItems, recentBytes) = CleanupStatsStore.RecentTotals(stats);

            StatsItems.Value = stats.ItemsRemoved.ToString("N0");
            StatsItems.Detail = recentItems > 0
                ? $"{recentItems:N0} in the last 30 days"
                : "since CleanMachine was installed";

            StatsSpace.Value = AppNotifications.FormatBytes(stats.BytesRecovered);
            StatsSpace.Detail = recentBytes > 0
                ? $"{AppNotifications.FormatBytes(recentBytes)} in the last 30 days"
                : "since CleanMachine was installed";

            StatsLast.Value = stats.LastCleanup == DateTimeOffset.MinValue
                ? "Never"
                : Relative(stats.LastCleanup);
            StatsLast.Detail = stats.LastCleanup == DateTimeOffset.MinValue
                ? "Run a cleanup to get started"
                : stats.LastCleanup.LocalDateTime.ToString("d MMM yyyy, HH:mm");
        }
        catch { /* stats are best-effort; cards keep their placeholders */ }
    }

    private static string Relative(DateTimeOffset time)
    {
        var elapsed = DateTimeOffset.UtcNow - time;
        if (elapsed < TimeSpan.FromMinutes(1)) return "Just now";
        if (elapsed < TimeSpan.FromHours(1)) return $"{(int)elapsed.TotalMinutes} min ago";
        if (elapsed < TimeSpan.FromDays(1)) return $"{(int)elapsed.TotalHours} h ago";
        if (elapsed < TimeSpan.FromDays(2)) return "Yesterday";
        return $"{(int)elapsed.TotalDays} days ago";
    }

    private void OpenCleaner_Click(object sender, RoutedEventArgs e) => ((MainWindow)App.MainWindow!).Navigate<CleanerPage>();
    private void OpenRegistry_Click(object sender, RoutedEventArgs e) => ((MainWindow)App.MainWindow!).Navigate<RegistryCarePage>();
    private void OpenWindows_Click(object sender, RoutedEventArgs e) => ((MainWindow)App.MainWindow!).Navigate<WindowsCleanupPage>();
    private void OpenApps_Click(object sender, RoutedEventArgs e) => ((MainWindow)App.MainWindow!).Navigate<AppCleanupPage>();

    // ---- Quick Clean: one immediate clean per area, using the saved selection ----

    private async void BrowsersQuickClean_Click(object sender, RoutedEventArgs e)
        => await RunQuickCleanAsync(QuickCleanArea.Browsers, (Button)sender, BrowsersProgress, BrowsersResult);

    private async void WindowsQuickClean_Click(object sender, RoutedEventArgs e)
        => await RunQuickCleanAsync(QuickCleanArea.Windows, (Button)sender, WindowsProgress, WindowsResult);

    private async void RegistryQuickClean_Click(object sender, RoutedEventArgs e)
        => await RunQuickCleanAsync(QuickCleanArea.Registry, (Button)sender, RegistryProgress, RegistryResult);

    private async void AppsQuickClean_Click(object sender, RoutedEventArgs e)
        => await RunQuickCleanAsync(QuickCleanArea.Apps, (Button)sender, AppsProgress, AppsResult);

    private async Task RunQuickCleanAsync(QuickCleanArea area, Button button, ProgressBar progress, TextBlock result)
    {
        button.IsEnabled = false;
        progress.Visibility = Visibility.Visible;
        result.Visibility = Visibility.Collapsed;
        try
        {
            var outcome = await QuickCleanService.RunAsync(area);
            result.Visibility = Visibility.Visible;
            // SummaryText owns the wording so a run that cleaned nothing still says
            // why, the same way its activity entry does.
            result.Text = QuickCleanService.SummaryText(outcome);
            await LoadStatsAsync();
            _ = LoadAvailabilityAsync(forceRefresh: true); // refresh the summaries in the background
        }
        catch (Exception ex)
        {
            result.Visibility = Visibility.Visible;
            result.Text = $"Quick Clean failed: {ex.Message}";
        }
        finally
        {
            button.IsEnabled = true;
            progress.Visibility = Visibility.Collapsed;
        }
    }

    // ---- Gear buttons: choose which items each area's Quick Clean includes ----

    private async void BrowsersSettings_Click(object sender, RoutedEventArgs e)
    {
        var settings = await AppSettings.LoadAsync();
        // Only the browsers actually present on this PC. Listing every supported one
        // on a machine that has two of them just adds rows that can never do
        // anything. Internet Explorer is excluded because its cache is the Windows
        // Internet Cache, already a Windows Cleanup category.
        var installed = await Task.Run(() => BrowserCatalog.InstalledCacheBrowsers());
        var chosen = await ItemPicker.ShowAsync(XamlRoot, "Quick Clean - Browser Caches",
            "Tick the browsers whose cache Quick Clean clears.",
            [
                new PickerSection("browsers", "Browser Cleaner - Internet Cache",
                    "Cache only - history, cookies, passwords and other saved data are never touched, and the browser can stay open (files it is using are skipped).",
                    [new PickerGroup(null, installed.Select(b => new PickerItem(b.Id, b.Name, settings.QuickCleanBrowsers.Contains(b.Id), "Internet Cache")).ToList())],
                    EmptyNote: "No supported browsers were found on this PC.")
            ]);
        if (chosen is null) return;

        // Hiding a row must not quietly delete a stored preference: a browser that is
        // ticked but not installed keeps its tick, so installing it later brings the
        // choice back.
        var selected = chosen["browsers"];
        selected.UnionWith(settings.QuickCleanBrowsers.Where(id => installed.All(b => !b.Id.Equals(id, StringComparison.OrdinalIgnoreCase))));
        await SaveSelectionAsync(s => s.QuickCleanBrowsers = selected);
    }

    private async void WindowsSettings_Click(object sender, RoutedEventArgs e)
    {
        var settings = await AppSettings.LoadAsync();
        PickerItem Row(CleanupCategory c) => new(c.Id, c.Name, QuickCleanService.IsWindowsSelected(c, settings), c.Description);
        var safe = WindowsCleanupService.Catalog.Where(c => c.Risk == CleanupRisk.Safe);
        var recycleBin = WindowsCleanupService.Catalog.Where(c => c.Id == QuickCleanService.RecycleBinCategoryId);

        var chosen = await ItemPicker.ShowAsync(XamlRoot, "Quick Clean - Windows Cleanup",
            "Tick the Windows Cleanup categories Quick Clean removes.",
            [
                new PickerSection("windows", "Windows Cleanup",
                    "Safe categories that Windows recreates on demand.",
                    safe.GroupBy(c => c.Group).Select(g => new PickerGroup(g.Key, g.Select(Row).ToList())).ToList()),
                // Its own card, outside Select All: ticking it is the confirmation its
                // Review risk requires.
                new PickerSection("review", "Recycle Bin",
                    "Permanently deletes everything in the Recycle Bin each time Quick Clean runs. Never part of Select All.",
                    [new PickerGroup(null, recycleBin.Select(Row).ToList())],
                    InSelectAll: false,
                    IsWarning: true)
            ]);
        if (chosen is null) return;

        var selected = chosen["windows"];
        selected.UnionWith(chosen["review"]);
        await SaveSelectionAsync(s => s.QuickCleanWindowsCategories = selected);
    }

    private async void RegistrySettings_Click(object sender, RoutedEventArgs e)
    {
        var settings = await AppSettings.LoadAsync();
        var current = settings.QuickCleanRegistryCategories;
        var chosen = await ItemPicker.ShowAsync(XamlRoot, "Quick Clean - Registry Care",
            "Tick the kinds of registry findings Quick Clean fixes.",
            [
                new PickerSection("registry", "Registry Care",
                    "Only low-risk findings are removed, and a backup you can restore from is always taken first.",
                    [new PickerGroup(null, QuickCleanService.RegistryCategories
                        .Select(cat => new PickerItem(cat, cat, current is null || current.Contains(cat)))
                        .ToList())])
            ]);
        if (chosen is null) return;

        var selected = chosen["registry"];
        await SaveSelectionAsync(s => s.QuickCleanRegistryCategories = selected);
    }

    private async void AppsSettings_Click(object sender, RoutedEventArgs e)
    {
        var settings = await AppSettings.LoadAsync();
        var current = settings.QuickCleanApps; // null = every installed app
        var installed = await Task.Run(() => AppCleanupService.InstalledApps());
        var chosen = await ItemPicker.ShowAsync(XamlRoot, "Quick Clean - Application Temp Files",
            "Tick the apps whose temp files Quick Clean removes.",
            [
                new PickerSection("apps", "Application Cleanup - Temp Files",
                    "The temporary, cache and log files listed for each app on the Application Cleanup page.",
                    installed.GroupBy(a => a.Group)
                        .Select(g => new PickerGroup(g.Key, g.Select(a => new PickerItem(a.Id, a.Name, current is null || current.Contains(a.Id))).ToList()))
                        .ToList(),
                    EmptyNote: "No supported applications were found on this PC.")
            ]);
        if (chosen is null) return;

        var selected = chosen["apps"];
        if (current is null && selected.Count == installed.Count)
        {
            // Everything still ticked: keep "every app", so apps installed later are
            // included too, as before.
            await SaveSelectionAsync(s => s.QuickCleanApps = null);
            return;
        }
        // Keep ticks for apps the dialog could not show (not installed now).
        if (current is not null)
            selected.UnionWith(current.Where(id => installed.All(a => !a.Id.Equals(id, StringComparison.OrdinalIgnoreCase))));
        await SaveSelectionAsync(s => s.QuickCleanApps = selected);
    }

    /// <summary>Applies a picker's result to freshly loaded settings and saves them,
    /// so a change made elsewhere while the dialog was open is not overwritten.</summary>
    private static async Task SaveSelectionAsync(Action<AppSettings> apply)
    {
        var settings = await AppSettings.LoadAsync();
        apply(settings);
        await settings.SaveAsync();
    }
}
