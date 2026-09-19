using Microsoft.UI.Xaml;

namespace CleanMachine.Windows;

public partial class App : Application
{
    private BackgroundAgent? _agent;
    private CancellationTokenSource? _agentCts;
    private Task? _agentTask;
    private int _agentGeneration;
    private readonly object _agentLock = new();
    private static readonly object AutomaticUpdateLock = new();
    private static DateTimeOffset? _automaticUpdateLastRun;
    private static readonly UpdateAutoInstaller AutomaticUpdateInstaller = new();
    private Mutex? _instanceMutex;
    private InstanceEvents? _instanceEvents;
    private Thread? _instanceEventsThread;
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiDispatcher;

    public static Window? MainWindow { get; private set; }

    /// <summary>True when this instance was launched by the logon startup entry
    /// (registered with <c>--background</c>), so the window should open straight to
    /// the tray instead of onto the desktop.</summary>
    public static bool LaunchedAtLogon { get; private set; }

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
        // A logon autostart launches with --background; open to the tray, not the desktop.
        LaunchedAtLogon = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase));
        // The listener runs on its own thread; UI work it triggers is posted through
        // the dispatcher captured here on the UI thread.
        _uiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        MainWindow = new MainWindow();
        MainWindow.Activate();
        AppNotifications.Register();
        StartInstanceEventsListener();

        // Sweep leftover update staging files from the install directory before
        // anything update-related can run (see CleanupUpdateArtifacts).
        UpdateService.CleanupUpdateArtifacts(Path.GetDirectoryName(Environment.ProcessPath));

        var settings = await AppSettings.LoadAsync();
        // Run startup cleanup before enabling periodic/background cleanup so the two
        // paths cannot mutate the same files concurrently on first launch.
        if (settings.CleanAtStartup)
            await RunSafeCleanAsync(settings, "Startup Cleanup", "At startup", settings.StartupCleanCategories, settings.StartupCleanNotify, CancellationToken.None);
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
                settings.ShouldStartWithWindows,
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
        Task? previous;
        int generation;
        lock (_agentLock)
        {
            generation = ++_agentGeneration;
            previous = StopBackgroundAgentCore();
        }
        if (previous is { IsCompleted: false })
        {
            // Cancellation stops the timer immediately, but an in-flight cleanup may
            // still be finishing. Do not start the replacement agent until that work
            // has completed, otherwise settings changes can create overlapping agents.
            _ = StartAgentAfterAsync(previous, generation);
            return;
        }

        lock (_agentLock)
        {
            if (generation == _agentGeneration)
                CreateBackgroundAgent();
        }
    }

    private async Task StartAgentAfterAsync(Task previous, int generation)
    {
        try { await previous.ConfigureAwait(false); }
        catch { /* the old agent is already being replaced */ }
        lock (_agentLock)
        {
            if (generation == _agentGeneration)
                CreateBackgroundAgent();
        }
    }

    private void CreateBackgroundAgent()
    {
        _agentCts = new CancellationTokenSource();
        _agent = new BackgroundAgent(
            onBrowserExit: OnBrowserExitAsync,
            onTick: OnAgentTickAsync);
        _agentTask = _agent.RunAsync(_agentCts.Token);
    }

    /// <summary>Runs the configured after-exit action for one specific browser.
    /// Only that browser's targets are cleaned, and only if monitoring for it
    /// is enabled and an action is selected.</summary>
    internal static bool TryReserveAutomaticUpdateCheck()
    {
        lock (AutomaticUpdateLock)
        {
            if (_automaticUpdateLastRun is { } last
                && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(6))
                return false;
            _automaticUpdateLastRun = DateTimeOffset.UtcNow;
            return true;
        }
    }

    private static async Task AutomaticUpdateTickAsync(AppSettings settings, CancellationToken token)
    {
        if (!settings.CheckForUpdatesAutomatically || !TryReserveAutomaticUpdateCheck()) return;

        try
        {
            var result = await new UpdateService().CheckAsync(token);
            if (settings.AutoInstallUpdates && result.Available)
                _ = AutomaticUpdateInstaller.TryInstallWhenIdleAsync(result, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A transient update-check failure must not terminate the cleanup agent.
        }
    }

    private static async Task OnBrowserExitAsync(string browser, CancellationToken token)
    {
        try
        {
            var settings = await AppSettings.LoadAsync(token);
            if (!settings.CleanOnBrowserExit) return;
            var monitor = settings.FindBrowserMonitor(browser);
            if (monitor is null || !monitor.Enabled || monitor.AfterExit == ExitAction.DoNothing) return;

            var cleanup = new BrowserCleanupService();
            // Clean exactly the items the user picked for this browser (see
            // EffectiveExitItems): the catalog's safe items unless configured
            // otherwise. The browser that just closed is gone, but other browsers
            // may still be open - their files are never part of this browser's
            // item paths, so skip the global all-browsers-closed check.
            var itemIds = settings.EffectiveExitItems(browser).ToArray();
            if (itemIds.Length == 0) return;

            // Give the OS a moment to release the cache files the browser had open.
            // Cleaning the instant the last process disappears often finds them still
            // locked - every file is skipped, so the pass reports nothing removed and
            // the "clean and notify" toast never fires. Wait, then retry once if the
            // first pass only hit locked files (no point retrying when there was
            // simply nothing to clean).
            CleanupReport report;
            var attempt = 0;
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token);
                report = await cleanup.CleanItemsAsync(
                    itemIds.Select(id => (browser, id)),
                    secureDelete: null, token, requireBrowsersClosed: false);
                if (report.Result.ItemsRemoved > 0 || report.Skipped.Count == 0 || ++attempt >= 2) break;
            }
            await new CleanupStatsStore().RecordAsync(report.Result.ItemsRemoved, report.Result.BytesRecovered, token);

            var displayName = char.ToUpperInvariant(browser[0]) + browser[1..];
            if (monitor.AfterExit == ExitAction.CleanAndNotify && report.Result.ItemsRemoved > 0)
                AppNotifications.ShowCleanupComplete(report.Result);
            await new ActivityStore().AddAsync(new ActivityEntry(
                DateTimeOffset.UtcNow,
                "Browser Monitoring",
                $"{displayName} closed - cleaned {report.Result.ItemsRemoved:N0} item(s), {AppNotifications.FormatBytes(report.Result.BytesRecovered)} recovered"),
                token);
        }
        catch { /* monitoring is best-effort; never let it kill the agent loop */ }
    }

    private static DateTimeOffset? _systemMonitorLastRun;
    private static bool _systemMonitorArmed = true;
    private static bool _idleCleanArmed = true;
    private static DateTimeOffset? _recycleBinLastRun;

    /// <summary>Runs every agent tick: the independent automatic-cleanup checks each
    /// decide for themselves whether to act, so one being off never blocks another.</summary>
    private static async Task OnAgentTickAsync(CancellationToken token)
    {
        AppSettings settings;
        try { settings = await AppSettings.LoadAsync(token); }
        catch { return; }
        await AutomaticUpdateTickAsync(settings, token);
        await SystemMonitorTickAsync(settings, token);
        await IdleCleanTickAsync(settings, token);
        await RecycleBinTickAsync(settings, token);
    }

    /// <summary>The Safe-risk categories an automatic clean removes: the given chosen
    /// set when configured, otherwise every Safe category enabled on the Windows
    /// Cleanup page. Review/Advanced categories are never included, so an unattended
    /// run can never touch them.</summary>
    private static List<CleanupCategory> SelectedSafeCategories(AppSettings settings, IReadOnlySet<string>? categoryIds) =>
        WindowsCleanupService.Catalog
            .Where(c => c.Risk == CleanupRisk.Safe
                && (categoryIds is { } set
                    ? set.Contains(c.Id)
                    : WindowsCleanupService.IsEnabled(c, settings)))
            .ToList();

    /// <summary>Cleans the given category selection and logs the result (with a
    /// per-category breakdown) when anything was removed. Shared by the startup and
    /// idle triggers; shows a toast only when the trigger's notify option is on
    /// (off by default, so automatic runs stay quiet in the background).</summary>
    private static async Task RunSafeCleanAsync(AppSettings settings, string activityTitle, string reason, IReadOnlySet<string>? categoryIds, bool notify, CancellationToken token)
    {
        var selected = SelectedSafeCategories(settings, categoryIds);
        if (selected.Count == 0) return;
        var report = await new WindowsCleanupService().CleanSelectedAsync(
            selected,
            new WindowsCleanupOptions(ConfirmReviewCategories: false, ExcludedPaths: settings.ExcludedPaths),
            cancellationToken: token);
        await new CleanupStatsStore().RecordAsync(report.Result.ItemsRemoved, report.Result.BytesRecovered, token);
        if (report.Result.ItemsRemoved > 0)
        {
            if (notify)
                AppNotifications.ShowAutomaticCleanupComplete(activityTitle, report.Result);
            await new ActivityStore().AddAsync(new ActivityEntry(
                DateTimeOffset.UtcNow,
                activityTitle,
                $"{reason} - cleaned {report.Result.ItemsRemoved:N0} item(s), {AppNotifications.FormatBytes(report.Result.BytesRecovered)} recovered",
                ActivityStore.BreakdownLines(report.Breakdown)), token);
        }
    }

    /// <summary>Run a safe clean after the machine has been idle for the configured
    /// time. Fires once per idle period and re-arms after the next activity.</summary>
    private static async Task IdleCleanTickAsync(AppSettings settings, CancellationToken token)
    {
        try
        {
            if (!settings.IdleCleanEnabled) { _idleCleanArmed = true; return; }
            if (IdleTime().TotalMinutes < Math.Max(1, settings.IdleCleanMinutes)) { _idleCleanArmed = true; return; }
            if (!_idleCleanArmed) return;
            _idleCleanArmed = false; // one clean per idle period
            await RunSafeCleanAsync(settings, "Idle Cleanup", $"Idle {Math.Max(1, settings.IdleCleanMinutes)}+ min", settings.IdleCleanCategories, settings.IdleCleanNotify, token);
        }
        catch { /* best-effort; never kill the agent loop */ }
    }

    /// <summary>Empty Recycle Bin items older than the configured age, at most once
    /// per hour.</summary>
    private static async Task RecycleBinTickAsync(AppSettings settings, CancellationToken token)
    {
        try
        {
            if (!settings.RecycleBinAutoEmptyEnabled) return;
            if (_recycleBinLastRun is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(1)) return;
            _recycleBinLastRun = DateTimeOffset.UtcNow;
            await CleanupCoordinator.Gate.WaitAsync(token);
            (int removed, long bytes) result;
            try
            {
                result = await Task.Run(() => RecycleBinService.EmptyOlderThan(settings.RecycleBinAutoEmptyDays, token), token);
            }
            finally
            {
                CleanupCoordinator.Gate.Release();
            }
            var (removed, bytes) = result;
            if (removed > 0)
            {
                await new CleanupStatsStore().RecordAsync(removed, bytes, token);
                if (settings.RecycleBinAutoEmptyNotify)
                    AppNotifications.ShowAutomaticCleanupComplete("Recycle Bin", removed, bytes);
                await new ActivityStore().AddAsync(new ActivityEntry(
                    DateTimeOffset.UtcNow,
                    "Recycle Bin",
                    $"Emptied {removed:N0} item(s) older than {settings.RecycleBinAutoEmptyDays} day(s), {AppNotifications.FormatBytes(bytes)} recovered"),
                    token);
            }
        }
        catch { /* best-effort */ }
    }

    // Idle time since the last keyboard/mouse input. Internal so the update
    // auto-installer can wait for a silent-install idle window too.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Time; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    internal static TimeSpan IdleTime()
    {
        var info = new LastInputInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
    }

    /// <summary>System monitoring: when free space on the Windows drive is below
    /// the threshold, clean the enabled safe categories. Fires at most once per
    /// hour and re-arms only after free space recovers above the threshold.</summary>
    private static async Task SystemMonitorTickAsync(AppSettings settings, CancellationToken token)
    {
        try
        {
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
            // categories always stay behind the manual page.
            var selected = SelectedSafeCategories(settings, settings.SystemMonitorCategories);
            if (selected.Count == 0) return;
            var report = await new WindowsCleanupService().CleanSelectedAsync(
                selected,
                new WindowsCleanupOptions(ConfirmReviewCategories: false, ExcludedPaths: settings.ExcludedPaths),
                cancellationToken: token);

            _systemMonitorLastRun = DateTimeOffset.UtcNow;
            _systemMonitorArmed = false; // wait for recovery before firing again
            await new CleanupStatsStore().RecordAsync(report.Result.ItemsRemoved, report.Result.BytesRecovered, token);

            if (settings.SystemMonitorAction == ExitAction.CleanAndNotify && report.Result.ItemsRemoved > 0)
                AppNotifications.ShowSystemCleanupComplete(report.Result);
            var usingMb = string.Equals(settings.SystemMonitorFreeSpaceUnit, "MB", StringComparison.OrdinalIgnoreCase);
            var thresholdLabel = usingMb ? $"{thresholdGb * 1024.0:0.#} MB" : $"{thresholdGb:0.#} GB";
            await new ActivityStore().AddAsync(new ActivityEntry(
                DateTimeOffset.UtcNow,
                "System Monitoring",
                $"Free space below {thresholdLabel} - cleaned {report.Result.ItemsRemoved:N0} items, {AppNotifications.FormatBytes(report.Result.BytesRecovered)} recovered",
                ActivityStore.BreakdownLines(report.Breakdown)),
                token);
        }
        catch { /* monitoring is best-effort; never let it kill the agent loop */ }
    }

    public void StopBackgroundAgent()
    {
        Task? task;
        lock (_agentLock)
        {
            ++_agentGeneration;
            task = StopBackgroundAgentCore();
        }
        // Observe (don't block on) the loop's completion: it swallows cancellation,
        // and blocking here would deadlock against its UI-context continuations.
        _ = task?.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    private Task? StopBackgroundAgentCore()
    {
        _agentCts?.Cancel();
        var task = _agentTask;
        _agentTask = null;
        _agent?.Dispose();
        _agent = null;
        _agentCts = null;
        return task;
    }
}
