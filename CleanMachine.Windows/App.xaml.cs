using Microsoft.UI.Xaml;

namespace CleanMachine.Windows;

public partial class App : Application
{
    private BackgroundAgent? _agent;
    private CancellationTokenSource? _agentCts;

    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
        AppNotifications.Register();

        var settings = await AppSettings.LoadAsync();
        if (settings.BackgroundAgentEnabled)
            StartBackgroundAgent(settings);
    }

    public void StartBackgroundAgent(AppSettings? settings = null)
    {
        StopBackgroundAgent();
        _agentCts = new CancellationTokenSource();
        _agent = new BackgroundAgent(
            onBrowserExit: OnBrowserExitAsync,
            onTick: OnAgentTickAsync);
        _ = _agent.RunAsync(_agentCts.Token);
    }

    /// <summary>Runs the configured after-exit action for one specific browser.
    /// Only that browser's targets are cleaned, and only if monitoring for it
    /// is enabled and an action is selected.</summary>
    private static async Task OnBrowserExitAsync(string browser, CancellationToken token)
    {
        try
        {
            var settings = await AppSettings.LoadAsync(token);
            if (!settings.CleanOnBrowserExit) return;
            var monitor = settings.FindBrowserMonitor(browser);
            if (monitor is null || !monitor.Enabled || monitor.AfterExit == ExitAction.DoNothing) return;

            var cleanup = new BrowserCleanupService();
            var targets = await cleanup.ScanAsync([browser], token: token);
            var result = await cleanup.CleanAsync(targets, requireBrowsersClosed: false, token);

            var displayName = char.ToUpperInvariant(browser[0]) + browser[1..];
            if (monitor.AfterExit == ExitAction.CleanAndNotify && result.ItemsRemoved > 0)
                AppNotifications.ShowCleanupComplete(result);
            await new ActivityStore().AddAsync(new ActivityEntry(
                DateTimeOffset.UtcNow,
                "Browser monitoring",
                $"{displayName} closed - cache cleaned ({result.ItemsRemoved:N0} items, {AppNotifications.FormatBytes(result.BytesRecovered)} recovered)"),
                token);
        }
        catch { /* monitoring is best-effort; never let it kill the agent loop */ }
    }

    /// <summary>System monitoring: when free space on the Windows drive is below
    /// the threshold, clean the enabled safe categories. Fires at most once per
    /// hour and re-arms only after free space recovers above the threshold.</summary>
    private static DateTimeOffset? _systemMonitorLastRun;
    private static bool _systemMonitorArmed = true;

    private static async Task OnAgentTickAsync(CancellationToken token)
    {
        try
        {
            var settings = await AppSettings.LoadAsync(token);
            if (!settings.SystemMonitoringEnabled)
            {
                _systemMonitorArmed = true;
                return;
            }

            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
            var thresholdGb = Math.Max(settings.SystemMonitorFreeSpaceGb, 0.1);

            if (freeGb >= thresholdGb)
            {
                _systemMonitorArmed = true; // recovered: allow the next dip to fire
                return;
            }
            if (!_systemMonitorArmed) return;
            if (_systemMonitorLastRun is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(1))
                return;

            // Only Safe-risk categories the user has enabled are ever cleaned here;
            // Review/Advanced categories always stay behind the manual page.
            var selected = WindowsCleanupService.Catalog
                .Where(c => c.Risk == CleanupRisk.Safe && WindowsCleanupService.IsEnabled(c, settings))
                .ToList();
            var report = await new WindowsCleanupService().CleanSelectedAsync(
                selected,
                new WindowsCleanupOptions(ConfirmReviewCategories: false, AllowElevation: false, ExcludedPaths: settings.ExcludedPaths),
                cancellationToken: token);

            _systemMonitorLastRun = DateTimeOffset.UtcNow;
            _systemMonitorArmed = false; // wait for recovery before firing again

            if (settings.SystemMonitorAction == ExitAction.CleanAndNotify && report.Result.ItemsRemoved > 0)
                AppNotifications.ShowSystemCleanupComplete(report.Result);
            await new ActivityStore().AddAsync(new ActivityEntry(
                DateTimeOffset.UtcNow,
                "System monitoring",
                $"Free space below {thresholdGb:0.#} GB - cleaned {report.Result.ItemsRemoved:N0} items, {AppNotifications.FormatBytes(report.Result.BytesRecovered)} recovered"),
                token);
        }
        catch { /* monitoring is best-effort; never let it kill the agent loop */ }
    }

    public void StopBackgroundAgent()
    {
        _agentCts?.Cancel();
        _agent?.Dispose();
        _agent = null;
        _agentCts = null;
    }
}
