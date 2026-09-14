using Microsoft.UI.Xaml;

namespace CleanMachine.Windows;

public partial class App : Application
{
    private BackgroundAgent? _agent;
    private CancellationTokenSource? _agentCts;
    private Mutex? _instanceMutex;
    private InstanceEvents? _instanceEvents;
    private Thread? _instanceEventsThread;
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiDispatcher;

    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Headless entry point used by Windows Scheduled Tasks. No window is created;
        // the process runs the cleanup, records activity, and exits.
        if (TryReadScheduledRun(Environment.GetCommandLineArgs(), out var scheduleId))
        {
            await RunScheduledCleanupAsync(scheduleId);
            Exit();
            return;
        }

        // The installer/uninstaller launches the app with --shutdown to ask a running
        // copy to exit before replacing or deleting its files. This process never
        // becomes a GUI instance; it just delivers the request and force-kills as a
        // last resort, so Setup can immediately proceed.
        if (HasShutdownArgument(Environment.GetCommandLineArgs()))
        {
            SingleInstance.HandleShutdownArgument();
            Exit();
            return;
        }

        // Single GUI instance: a second launch signals the running copy to show its
        // window (out of the tray if needed) and exits instead of forking a process.
        if (SingleInstance.TryAcquire() is not { } instanceMutex)
        {
            SingleInstance.RequestOtherInstanceActivate();
            Exit();
            return;
        }
        _instanceMutex = instanceMutex;
        // The listener runs on its own thread; UI work it triggers is posted through
        // the dispatcher captured here on the UI thread.
        _uiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        MainWindow = new MainWindow();
        MainWindow.Activate();
        AppNotifications.Register();
        StartInstanceEventsListener();

        var settings = await AppSettings.LoadAsync();
        if (settings.RequiresBackgroundAgent)
            StartBackgroundAgent(settings);
        // Keep the OS task store in step with whatever schedules are saved.
        _ = ScheduleService.SyncAllAsync(settings);
    }

    private static bool HasShutdownArgument(string[] arguments) =>
        arguments.Any(a => a.Equals("--shutdown", StringComparison.OrdinalIgnoreCase));

    /// <summary>Starts the background thread that waits on the named shutdown and
    /// activate events (owned by this instance) so the installer - or a second app
    /// launch - can reach a running copy that is sitting in the tray with no window.</summary>
    private void StartInstanceEventsListener()
    {
        if (_instanceEvents is not null) return;
        _instanceEvents = SingleInstance.TryCreateEvents();
        if (_instanceEvents is null) return; // signalling unavailable; the app still runs
        _instanceEventsThread = new Thread(RunInstanceEventListener)
        {
            IsBackground = true, // never keep the process alive on its own
            Name = "CleanMachine.InstanceEvents"
        };
        _instanceEventsThread.Start();
    }

    private void RunInstanceEventListener()
    {
        try
        {
            var events = _instanceEvents!;
            var handles = new WaitHandle[] { events.Shutdown, events.Activate };
            while (true)
            {
                switch (WaitHandle.WaitAny(handles))
                {
                    case 0: // Shutdown: quit exactly like the tray menu's Exit.
                        // RequestExit alone: it sets the tray flag and then calls Exit()
                        // inside its own dispatcher callback, so the close-to-tray
                        // interception can never cancel the shutdown. Calling Exit()
                        // directly here would race the pending dispatcher work.
                        if (MainWindow is MainWindow window)
                            _uiDispatcher?.TryEnqueue(window.RequestExit);
                        else
                            Exit();
                        return;
                    case 1: // Activate: show and foreground the window.
                        _uiDispatcher?.TryEnqueue(() => (MainWindow as MainWindow)?.RequestShowFromTray());
                        break;
                    default:
                        return; // events disposed: stopping
                }
            }
        }
        catch { /* signalling is best-effort; never crash the listener */ }
    }

    /// <summary>Stops the IPC listener and releases the single-instance mutex; used
    /// when the app is asked to exit so a new instance can start immediately.</summary>
    public void StopInstanceEvents()
    {
        var thread = _instanceEventsThread;
        _instanceEventsThread = null;
        _instanceEvents?.Dispose(); // unblocks the listener's WaitAny
        _instanceEvents = null;
        thread?.Join(TimeSpan.FromSeconds(2));
        _instanceMutex?.Dispose();
        _instanceMutex = null;
    }

    private static bool TryReadScheduledRun(string[] arguments, out string scheduleId)
    {
        scheduleId = string.Empty;
        for (var i = 0; i < arguments.Length - 1; i++)
        {
            if (!arguments[i].Equals("--run-schedule", StringComparison.OrdinalIgnoreCase)) continue;
            scheduleId = arguments[i + 1];
            return !string.IsNullOrWhiteSpace(scheduleId);
        }
        return false;
    }

    private static async Task RunScheduledCleanupAsync(string scheduleId)
    {
        try
        {
            var settings = await AppSettings.LoadAsync();
            var schedule = settings.Schedules.FirstOrDefault(s =>
                s.Id.Equals(scheduleId, StringComparison.OrdinalIgnoreCase));
            if (schedule is null || !schedule.Enabled) return;
            await ScheduleService.RunAsync(schedule, settings);
        }
        catch { /* a headless run must never surface a dialog or crash the process */ }
    }

    /// <summary>Starts or stops the background agent to match the enabled services,
    /// and keeps Windows startup registration in step so the app is present to run
    /// them while the window is closed. Call this after any change to a service that
    /// the agent powers (browser-exit cleaning or the low-disk-space monitor).</summary>
    public void ApplyBackgroundServices(AppSettings settings)
    {
        try
        {
            StartupRegistration.SetEnabled(
                settings.RequiresBackgroundAgent,
                Environment.ProcessPath ?? string.Empty);
        }
        catch { /* startup registration is best-effort */ }

        if (settings.RequiresBackgroundAgent)
            StartBackgroundAgent(settings);
        else
            StopBackgroundAgent();
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
            _ = new CleanupStatsStore().RecordAsync(result.ItemsRemoved, result.BytesRecovered, token);

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
            var thresholdGb = Math.Max(settings.SystemMonitorFreeSpaceGb, 0.01);

            if (freeGb >= thresholdGb)
            {
                _systemMonitorArmed = true; // recovered: allow the next dip to fire
                return;
            }
            if (!_systemMonitorArmed) return;
            if (_systemMonitorLastRun is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(1))
                return;

            // Only Safe-risk categories are ever cleaned here; Review/Advanced
            // categories always stay behind the manual page. When the user has
            // chosen a specific set, honor it; otherwise fall back to every Safe
            // category enabled on the Windows Cleanup page.
            var selected = WindowsCleanupService.Catalog
                .Where(c => c.Risk == CleanupRisk.Safe
                    && (settings.SystemMonitorCategories is { } set
                        ? set.Contains(c.Id)
                        : WindowsCleanupService.IsEnabled(c, settings)))
                .ToList();
            if (selected.Count == 0) return;
            var report = await new WindowsCleanupService().CleanSelectedAsync(
                selected,
                new WindowsCleanupOptions(ConfirmReviewCategories: false, AllowElevation: false, ExcludedPaths: settings.ExcludedPaths),
                cancellationToken: token);

            _systemMonitorLastRun = DateTimeOffset.UtcNow;
            _systemMonitorArmed = false; // wait for recovery before firing again
            _ = new CleanupStatsStore().RecordAsync(report.Result.ItemsRemoved, report.Result.BytesRecovered, token);

            if (settings.SystemMonitorAction == ExitAction.CleanAndNotify && report.Result.ItemsRemoved > 0)
                AppNotifications.ShowSystemCleanupComplete(report.Result);
            var usingMb = string.Equals(settings.SystemMonitorFreeSpaceUnit, "MB", StringComparison.OrdinalIgnoreCase);
            var thresholdLabel = usingMb ? $"{thresholdGb * 1024.0:0.#} MB" : $"{thresholdGb:0.#} GB";
            await new ActivityStore().AddAsync(new ActivityEntry(
                DateTimeOffset.UtcNow,
                "System monitoring",
                $"Free space below {thresholdLabel} - cleaned {report.Result.ItemsRemoved:N0} items, {AppNotifications.FormatBytes(report.Result.BytesRecovered)} recovered"),
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
