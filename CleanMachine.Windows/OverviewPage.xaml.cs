using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanMachine.Windows;

public sealed partial class OverviewPage : Page
{
    // The last update-check result, so re-opening the page shows the outcome
    // immediately instead of a stuck "Checking for updates..." line.
    private static UpdateCheckResult? _lastAutoCheck;
    private static string _lastAutoCheckInstalled = "";

    private readonly UpdateService _updateService = new();
    // One instance for the app lifetime (Overview can be recreated on every
    // navigation); the installer itself serializes attempts and dedupes packages.
    private static UpdateAutoInstaller? _autoInstaller;

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

    private void AutoCheckForUpdates()
    {
        var installed = FormatVersion(UpdateService.CurrentVersion());

        // Show the previous result immediately; never a stuck "Checking..." line.
        if (_lastAutoCheck is { } last)
        {
            RenderUpdateResult(last, _lastAutoCheckInstalled);
        }
        else
        {
            UpdateStatusText.Text = "Updates";
            UpdateDetailText.Text = $"CleanMachine {installed} is installed. Click 'Check for Updates' or wait for the automatic check.";
        }

        // The app-wide background scheduler owns the six-hour debounce. This page
        // also participates in that scheduler so opening Overview does not create
        // a duplicate request when the agent is already running.
        if (!App.TryReserveAutomaticUpdateCheck()) return;
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
            var result = await _updateService.CheckAsync();
            _lastAutoCheck = result;
            _lastAutoCheckInstalled = installed;
            RenderUpdateResult(result, installed);
            // Idle auto-install only for background checks: a manual check means
            // the user is present and gets the click-to-install flow.
            if (!manual) await MaybeAutoInstallAsync(result);
        }
        catch (Exception ex)
        {
            UpdateNowButton.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = "Update check failed";
            UpdateDetailText.Text = $"{ex.Message} CleanMachine {installed} is installed.";
        }
        finally
        {
            UpdateCheckButton.IsEnabled = true;
            UpdateProgress.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>When the idle auto-install setting is on, hands an available
    /// update found by an automatic check (never a manual one) to the
    /// UpdateAutoInstaller, which waits for an idle window and installs it
    /// silently. Manual checks always stay click-to-install.</summary>
    private async Task MaybeAutoInstallAsync(UpdateCheckResult result)
    {
        var settings = await AppSettings.LoadAsync();
        if (!settings.AutoInstallUpdates || !result.Available) return;
        // The installer is stateless apart from its session guard, so one
        // instance serves the app lifetime (Overview can be recreated on every
        // navigation).
        _autoInstaller ??= new UpdateAutoInstaller();
        _ = _autoInstaller.TryInstallWhenIdleAsync(result);
    }

    private void RenderUpdateResult(UpdateCheckResult result, string installed)
    {
        if (result.Error is not null)
        {
            UpdateNowButton.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = "Update check failed";
            UpdateDetailText.Text = $"{result.Error} CleanMachine {installed} is installed.";
            return;
        }
        if (!result.Available)
        {
            UpdateNowButton.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = "You're up to date";
            UpdateDetailText.Text = $"CleanMachine {installed} is installed.";
            return;
        }

        var manifest = result.Manifest!;
        UpdateStatusText.Text = $"Version {manifest.Version} available";
        UpdateDetailText.Text = $"CleanMachine {installed} is installed. Click Update Now to download and install.";
        UpdateNowButton.Visibility = Visibility.Visible;
    }

    private static string FormatVersion(Version version) => $"v{version.Major}.{version.Minor}.{version.Build}";

    /// <summary>Downloads, verifies and installs the available update directly from the
    /// Overview page - the same one-click flow as the Updates page.</summary>
    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        var package = _lastAutoCheck?.Package;
        if (package is null) { UpdateStatusText.Text = "Run Check for Updates first."; return; }
        var manifest = _lastAutoCheck?.Manifest;

        var confirm = new ContentDialog
        {
            Title = manifest is not null ? $"Update to version {manifest.Version}?" : "Update now?",
            Content = (manifest is not null && !string.IsNullOrWhiteSpace(manifest.ReleaseNotes) ? manifest.ReleaseNotes + "\n\n" : "")
                + "CleanMachine will download, verify and install the update, then restart. Windows may ask for administrator permission.",
            PrimaryButtonText = "Update Now",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        UpdateNowButton.IsEnabled = false;
        UpdateCheckButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.IsIndeterminate = false;
        UpdateProgress.Value = 0;
        try
        {
            var download = new Progress<double>(p =>
            {
                UpdateProgress.Value = p;
                UpdateStatusText.Text = $"Downloading update... {p:P0}";
            });
            UpdateStatusText.Text = "Downloading update...";
            var path = await _updateService.DownloadAndVerifyAsync(package, download, targetVersion: manifest?.Version);

            UpdateProgress.IsIndeterminate = true;
            UpdateStatusText.Text = "Verified. Installing...";
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Application path not found.");
            var isExe = path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            await _updateService.InstallVerifiedPackageAsync(path, executable);
            UpdateNowButton.Visibility = Visibility.Collapsed;
            if (isExe)
            {
                UpdateStatusText.Text = "Installer launched. CleanMachine will now close to finish updating.";
                Microsoft.UI.Xaml.Application.Current.Exit();
            }
            else
            {
                UpdateStatusText.Text = _updateService.MsixInstallHandedOff
                    ? "Installing update - CleanMachine will close and reopen shortly."
                    : "Update installed. Please restart CleanMachine.";
            }
        }
        catch (OperationCanceledException ex)
        {
            // e.g. UAC declined - Update Now stays available. The headline does not
            // wrap, so it stays short and the full reason goes in the wrapped detail
            // line beneath it, matching how a failed check is reported above.
            UpdateStatusText.Text = "Update didn't start";
            UpdateDetailText.Text = $"{ex.Message} CleanMachine {FormatVersion(UpdateService.CurrentVersion())} is installed.";
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "Update failed";
            UpdateDetailText.Text = $"{ex.Message} CleanMachine {FormatVersion(UpdateService.CurrentVersion())} is installed.";
        }
        finally
        {
            UpdateNowButton.IsEnabled = true;
            UpdateCheckButton.IsEnabled = true;
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateProgress.IsIndeterminate = false;
        }
    }

    private void OpenCleaner_Click(object sender, RoutedEventArgs e) => ((MainWindow)App.MainWindow!).Navigate<CleanerPage>();
    private void OpenRegistry_Click(object sender, RoutedEventArgs e) => ((MainWindow)App.MainWindow!).Navigate<RegistryCarePage>();
    private void OpenWindows_Click(object sender, RoutedEventArgs e) => ((MainWindow)App.MainWindow!).Navigate<WindowsCleanupPage>();
    private void OpenApps_Click(object sender, RoutedEventArgs e) => ((MainWindow)App.MainWindow!).Navigate<AppCleanupPage>();
    private void OpenUpdates_Click(object sender, RoutedEventArgs e) => ((MainWindow)App.MainWindow!).Navigate<UpdatesPage>();

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
