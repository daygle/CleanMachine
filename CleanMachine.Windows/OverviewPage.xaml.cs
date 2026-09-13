using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CleanMachine.Windows;

public sealed partial class OverviewPage : Page
{
    // Automatic update checks are debounced: at most one per 6 hours per app
    // session, no matter how often the Overview page is opened.
    private static DateTimeOffset? _autoCheckLastRun;

    // Cancel for an in-flight 'Clean All Safe Items' run.
    private CancellationTokenSource? _cleanAllCts;

    public OverviewPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => { await LoadStatsAsync(); await LoadAvailabilityAsync(); AutoCheckForUpdates(); };
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
            var (recentItems, recentBytes) = await CleanupStatsStore.RecentTotalsAsync();

            StatsItems.Value = stats.ItemsRemoved.ToString("N0");
            StatsItems.Detail = recentItems > 0
                ? $"{recentItems:N0} in the last 30 days"
                : "since CleanMachine was installed";

            StatsSpace.Value = WindowsCleanupPage.FormatBytes(stats.BytesRecovered);
            StatsSpace.Detail = recentBytes > 0
                ? $"{WindowsCleanupPage.FormatBytes(recentBytes)} in the last 30 days"
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

    private void AutoCheckForUpdates()
    {
        // At most one automatic check per 6 hours per app session.
        if (_autoCheckLastRun is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(6)) return;
        _autoCheckLastRun = DateTimeOffset.UtcNow;
        _ = RunUpdateCheckAsync(manual: false);
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await RunUpdateCheckAsync(manual: true);

    /// <summary>Runs an update check and reflects the outcome in the status card:
    /// installed version when current, or the available version and release notes
    /// when an update exists (installing stays on the Updates page).</summary>
    private async Task RunUpdateCheckAsync(bool manual)
    {
        var settings = await AppSettings.LoadAsync();
        var installed = FormatVersion(UpdateService.CurrentVersion());

        if (!manual && !settings.CheckForUpdatesAutomatically)
        {
            UpdateStatusText.Text = "Automatic update checks are off";
            UpdateDetailText.Text = $"CleanMachine {installed} is installed. Use 'Check for Updates' to check now.";
            return;
        }

        UpdateCheckButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        try
        {
            var result = await new UpdateService().CheckAsync();
            if (result.Error is not null)
            {
                UpdateStatusText.Text = "Update check failed";
                UpdateDetailText.Text = $"{result.Error} CleanMachine {installed} is installed.";
                return;
            }
            if (!result.Available)
            {
                UpdateStatusText.Text = "You're up to date";
                UpdateDetailText.Text = $"CleanMachine {installed} is installed.";
                return;
            }

            var manifest = result.Manifest!;
            UpdateStatusText.Text = $"Version {manifest.Version} available";
            UpdateDetailText.Text = string.IsNullOrWhiteSpace(manifest.ReleaseNotes)
                ? $"CleanMachine {installed} is installed. Open Updates to install version {manifest.Version}."
                : $"{manifest.ReleaseNotes} — open Updates to install version {manifest.Version}.";
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "Update check failed";
            UpdateDetailText.Text = $"{ex.Message} CleanMachine {installed} is installed.";
        }
        finally
        {
            UpdateCheckButton.IsEnabled = true;
            UpdateProgress.Visibility = Visibility.Collapsed;
        }
    }

    private static string FormatVersion(Version version) => $"v{version.Major}.{version.Minor}.{version.Build}";

    private void OpenCleaner_Click(object sender, RoutedEventArgs e) => ((MainWindow)App.MainWindow!).Navigate<CleanerPage>();
    private void OpenRegistry_Click(object sender, RoutedEventArgs e) => ((MainWindow)App.MainWindow!).Navigate<RegistryCarePage>();
    private void OpenWindows_Click(object sender, RoutedEventArgs e) => ((MainWindow)App.MainWindow!).Navigate<WindowsCleanupPage>();
    private void OpenApps_Click(object sender, RoutedEventArgs e) => ((MainWindow)App.MainWindow!).Navigate<AppCleanupPage>();

    private async void CleanAll_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanAllCts is not null) return; // a run is already in flight

        CleanAllButton.IsEnabled = false;
        CleanAllStatus.Visibility = Visibility.Visible;
        CleanAllStatus.Text = "Scanning every area…";
        CleanAllResult.Visibility = Visibility.Collapsed;
        CleanAllProgress.Visibility = Visibility.Visible;
        CleanAllCancelButton.Visibility = Visibility.Visible;
        _cleanAllCts = new CancellationTokenSource();

        try
        {
            var plan = await CleanAllService.BuildPlanAsync(_cleanAllCts.Token);
            if (!plan.HasWork)
            {
                CleanAllResult.Visibility = Visibility.Visible;
                CleanAllResult.Text = "Everything is already clean - nothing to do.";
                return;
            }

            var confirm = new ContentDialog
            {
                Title = "Clean all safe items?",
                Content = BuildPlanSummary(plan),
                PrimaryButtonText = "Clean",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            {
                CleanAllStatus.Visibility = Visibility.Collapsed;
                return;
            }

            var progress = new Progress<string>(text => CleanAllStatus.Text = text);
            var outcome = await CleanAllService.RunAsync(plan, progress, _cleanAllCts.Token);

            CleanAllStatus.Text = "Clean complete.";
            CleanAllResult.Visibility = Visibility.Visible;
            CleanAllResult.Text = BuildOutcomeSummary(outcome);
            await LoadStatsAsync();
            _ = LoadAvailabilityAsync(forceRefresh: true); // refresh the per-area cards in the background
        }
        catch (OperationCanceledException)
        {
            CleanAllStatus.Text = "Clean all cancelled.";
            CleanAllResult.Visibility = Visibility.Visible;
            CleanAllResult.Text = "Completed areas keep their results; the remaining areas were not cleaned.";
        }
        finally
        {
            _cleanAllCts.Dispose();
            _cleanAllCts = null;
            CleanAllButton.IsEnabled = true;
            CleanAllProgress.Visibility = Visibility.Collapsed;
            CleanAllCancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private void CleanAllCancel_Click(object sender, RoutedEventArgs e)
    {
        _cleanAllCts?.Cancel();
        CleanAllCancelButton.IsEnabled = false;
        CleanAllStatus.Text = "Cancelling after the current file…";
    }

    private static string BuildPlanSummary(CleanAllPlan plan)
    {
        var lines = new List<string>
        {
            $"CleanMachine will clean approximately {AppNotifications.FormatBytes(plan.TotalBytes)} ({plan.TotalItems:N0} item(s)) across:",
            string.Empty
        };
        foreach (var area in plan.Areas)
            lines.Add($"•  {area.Name} — {area.Detail}");
        lines.Add(string.Empty);
        lines.Add("Only safe items are included. Downloads, documents, Review/Advanced categories, and destructive browser items are never touched.");
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildOutcomeSummary(CleanAllOutcome outcome)
    {
        var text = $"{outcome.ItemsRemoved:N0} item(s) removed, {AppNotifications.FormatBytes(outcome.BytesRecovered)} recovered.";
        if (outcome.Issues.Count > 0)
            text += $" {outcome.Issues.Count} skipped (locked or protected files).";
        return text;
    }
}
