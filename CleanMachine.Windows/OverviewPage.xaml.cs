using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
            result.Text = outcome.Items == 0 && outcome.Bytes == 0 && outcome.Note is not null
                ? outcome.Note
                : $"{outcome.Items:N0} item(s) removed, {AppNotifications.FormatBytes(outcome.Bytes)} recovered"
                  + (outcome.Issues.Count > 0 ? $" - {outcome.Issues.Count} skipped." : ".");
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
        // Data-driven from the browser catalog so every supported browser (Chrome, Edge,
        // Brave, Vivaldi, Opera, Firefox) is selectable, not just the default three.
        // Internet Explorer is omitted: it has no profile cache of its own (its cache is
        // the Windows Internet Cache, already a Windows Cleanup category).
        var items = BrowserCatalog.Browsers
            .Where(b => b.Family != BrowserFamily.InternetExplorer)
            .Select(b => (b.Id, b.Name, settings.QuickCleanBrowsers.Contains(b.Id)))
            .ToList();
        await ShowPickerAsync("Browser Quick Clean", items,
            (s, selected) => s.QuickCleanBrowsers = selected);
    }

    private async void WindowsSettings_Click(object sender, RoutedEventArgs e)
    {
        var settings = await AppSettings.LoadAsync();
        var items = WindowsCleanupService.Catalog
            .Where(c => c.Risk == CleanupRisk.Safe || c.Id == QuickCleanService.RecycleBinCategoryId)
            .Select(c => (c.Id, c.Name, QuickCleanService.IsWindowsSelected(c, settings)))
            .ToList();
        await ShowPickerAsync("Windows Quick Clean", items,
            (s, selected) => s.QuickCleanWindowsCategories = selected);
    }

    private async void RegistrySettings_Click(object sender, RoutedEventArgs e)
    {
        var settings = await AppSettings.LoadAsync();
        var current = settings.QuickCleanRegistryCategories;
        var items = QuickCleanService.RegistryCategories
            .Select(cat => (cat, cat, current is null || current.Contains(cat)))
            .ToList();
        await ShowPickerAsync("Registry Quick Clean", items,
            (s, selected) => s.QuickCleanRegistryCategories = selected);
    }

    private async void AppsSettings_Click(object sender, RoutedEventArgs e)
    {
        var settings = await AppSettings.LoadAsync();
        var current = settings.QuickCleanApps;
        var items = AppCatalog.Definitions
            .Select(d => (d.Id, d.Name, current is null || current.Contains(d.Id)))
            .ToList();
        await ShowPickerAsync("Application Quick Clean", items,
            (s, selected) => s.QuickCleanApps = selected);
    }

    /// <summary>Shows a checkbox picker and, on Save, applies the chosen keys to
    /// settings and persists them. The ticked keys are written verbatim (an empty
    /// selection means "clean nothing for this area").</summary>
    private async Task ShowPickerAsync(
        string title,
        List<(string Key, string Label, bool Checked)> items,
        Action<AppSettings, HashSet<string>> apply)
    {
        var panel = new StackPanel { Spacing = 6 };
        var boxes = new List<(string Key, CheckBox Box)>();
        foreach (var (key, label, chk) in items)
        {
            var box = new CheckBox { Content = label, IsChecked = chk };
            boxes.Add((key, box));
            panel.Children.Add(box);
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 360 },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var selected = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var settings = await AppSettings.LoadAsync();
        apply(settings, selected);
        await settings.SaveAsync();
    }
}
