using System.Text.Json;
using System.Text.Json.Serialization;

namespace CleanMachine.Windows;

/// <summary>What should happen automatically when a monitored event fires
/// (a monitored browser closes, or free disk space crosses the threshold).</summary>
public enum ExitAction
{
    DoNothing = 0,
    CleanSilently = 1,
    CleanAndNotify = 2
}

/// <summary>Per-browser monitoring preferences: whether exit cleanup runs for
/// this browser, which action it takes, and which items the exit-clean covers.
/// <see cref="Items"/> holds explicit item ids; the safe-item resolver in
/// <see cref="AppSettings.EffectiveExitItems"/> decides what actually runs.</summary>
public sealed class BrowserMonitorSetting
{
    public string Browser { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public ExitAction AfterExit { get; set; } = ExitAction.CleanAndNotify;
    /// <summary>Item ids (from <see cref="BrowserCatalog"/>) the after-exit clean
    /// covers when explicitly configured. Null means "not configured yet" - the
    /// safe-item fallback applies. An empty set means the exit-clean does nothing
    /// for this browser.</summary>
    public HashSet<string>? Items { get; set; }
}

public sealed class AppSettings
{
    // Whether browser-exit cleanup runs at all. This is the "Monitor browsers"
    // switch on the Browser Cleaner page; the per-browser entries below refine it.
    public bool CleanOnBrowserExit { get; set; } = true;
    // Browser Cleaner page: when true, "Clean selected" closes running browsers
    // itself instead of refusing while any are open (graceful close first, then
    // force-kill for whatever is still alive after a 5s wait). Off by default,
    // which keeps the ask-first behavior of listing what must be closed.
    public bool CloseOpenBrowsersAutomatically { get; set; }
    public bool CheckForUpdatesAutomatically { get; set; } = true;
    // When true, the Updates flow installs without CleanMachine's own confirmation
    // dialog. Windows still shows its UAC elevation prompt for each install.
    public bool SkipUpdateConfirmation { get; set; }
    // When true, an update found by an automatic background check is downloaded,
    // verified, and installed silently once the machine is idle (no user input
    // for a while, nothing being cleaned). Requires CheckForUpdatesAutomatically;
    // manual checks never auto-install, and a per-machine install still shows its
    // UAC prompt because Windows requires it.
    public bool AutoInstallUpdates { get; set; }
    // Explicit "start with Windows" preference, independent of background services.
    // The app is registered for logon startup when this is set OR a background
    // service (browser-exit cleaning / low-disk monitoring) needs it.
    public bool StartWithWindows { get; set; }
    // When false the main window is hidden from the taskbar and, when minimized,
    // it collapses to a system-tray icon instead.
    public bool ShowInTaskbar { get; set; } = true;
    // Tray behavior: three independent options.
    // Start minimized to tray on launch.
    public bool StartMinimizedToTray { get; set; }
    // Close button minimizes to tray instead of exiting.
    public bool CloseToTray { get; set; }
    // Minimize button sends to tray (keeps taskbar button).
    public bool MinimizeToTray { get; set; } = true;
    // Application Cleanup page: when true, detected apps with nothing to clean
    // are shown in the list (greyed out) instead of being hidden.
    public bool ShowCleanApps { get; set; }
    public WipeMethod SecureDeleteMethod { get; set; } = WipeMethod.SimpleZeroFill;
    public int CustomWipePasses { get; set; } = 1;
    public HashSet<string> ProtectedBrowsers { get; set; } = ["chrome", "edge", "firefox"];
    public HashSet<string> ExcludedPaths { get; set; } = [];
    // Per-item Windows cleanup enable/disable overrides. A category is enabled when it is
    // listed here as enabled, or when it is enabled by default and not explicitly disabled.
    public HashSet<string> DisabledCleanupCategories { get; set; } = [];
    public HashSet<string> EnabledCleanupCategories { get; set; } = [];

    // Overview "Quick Clean" per-area item selections. Each area's gear button edits
    // its list; the Quick Clean button then cleans exactly those items immediately.
    // A null list means "not configured yet" - Quick Clean falls back to a safe
    // default (all currently-enabled Safe Windows categories, all registry
    // categories, all installed apps). Browsers default to the three supported ones.
    public HashSet<string> QuickCleanBrowsers { get; set; } = ["chrome", "edge", "firefox"];
    public HashSet<string>? QuickCleanWindowsCategories { get; set; }
    public HashSet<string>? QuickCleanRegistryCategories { get; set; }
    public HashSet<string>? QuickCleanApps { get; set; }

    // Remembered per-item tick state on the Browser Cleaner page, keyed by
    // "browserId:itemId". A key that is absent falls back to the default (safe
    // items ticked, destructive items unticked); a present key overrides it with
    // the user's last choice, so selections survive navigation and restarts.
    public Dictionary<string, bool> BrowserCleanupSelection { get; set; } = [];

    // Per-browser monitoring: entries use canonical ids (chrome, edge, firefox).
    public List<BrowserMonitorSetting> BrowserMonitors { get; set; } =
    [
        new() { Browser = "chrome" },
        new() { Browser = "edge" },
        new() { Browser = "firefox" }
    ];

    // System monitoring: clean safe categories when free disk space on the
    // Windows drive drops below the threshold. Opt-in; never fires more than
    // once per hour and only re-arms after free space recovers.
    public bool SystemMonitoringEnabled { get; set; }
    public double SystemMonitorFreeSpaceGb { get; set; } = 1.0;
    // Display unit for the threshold above, "GB" or "MB". The value itself is always
    // stored in GB; this is only which unit the Settings UI shows and edits in.
    public string SystemMonitorFreeSpaceUnit { get; set; } = "GB";
    public ExitAction SystemMonitorAction { get; set; } = ExitAction.CleanSilently;

    // Automatic cleanup: run a safe clean once each time CleanMachine starts (paired
    // with "Start with Windows" this cleans at every logon).
    public bool CleanAtStartup { get; set; }
    // Automatic cleanup: run a safe clean after the machine has been idle this many
    // minutes (fires once per idle period; re-arms after the next activity).
    public bool IdleCleanEnabled { get; set; }
    public int IdleCleanMinutes { get; set; } = 15;
    // Automatic cleanup: empty Recycle Bin items older than this many days.
    public bool RecycleBinAutoEmptyEnabled { get; set; }
    public int RecycleBinAutoEmptyDays { get; set; } = 30;
    // Which categories the startup and idle cleans remove. Each is independent; null
    // means every enabled category on the Windows Cleanup page. Automatic runs only
    // ever offer/clean Safe-risk categories (Review/Advanced are never included).
    public HashSet<string>? StartupCleanCategories { get; set; }
    public HashSet<string>? IdleCleanCategories { get; set; }
    // Which Safe Windows categories the low-disk-space monitor cleans. A null set
    // means "not configured" - it falls back to every Safe category enabled on the
    // Windows Cleanup page. An empty set means the user deselected everything, so
    // the monitor cleans nothing.
    public HashSet<string>? SystemMonitorCategories { get; set; }

    // The background agent has no standalone switch: it runs whenever a service
    // that needs it is enabled (automatic update checks or automatic cleanup).
    // Windows startup registration follows the same rule, so the app is present
    // while the window is closed. Derived, so it is never persisted.
    [JsonIgnore]
    public bool RequiresBackgroundAgent =>
        CheckForUpdatesAutomatically
        || CleanOnBrowserExit
        || SystemMonitoringEnabled
        || IdleCleanEnabled
        || RecycleBinAutoEmptyEnabled;

    // The app is registered to launch at logon when the user asked for it, when a
    // background service needs it running while the window is closed, or when a
    // startup clean is set (which only makes sense if the app starts at logon).
    // Derived, never persisted.
    [JsonIgnore]
    public bool ShouldStartWithWindows => StartWithWindows || RequiresBackgroundAgent || CleanAtStartup;

    // User-defined cleanup schedules, executed by Windows Task Scheduler so they run
    // even when the app is closed. An optional action (shutdown/restart/sleep) can
    // follow a successful run.
    public List<CleanupSchedule> Schedules { get; set; } = [];

    // Track whether we've created the desktop shortcut on first launch.
    public bool DesktopShortcutCreated { get; set; }

    public BrowserMonitorSetting? FindBrowserMonitor(string browser) =>
        BrowserMonitors.FirstOrDefault(m => m.Browser.Equals(browser, StringComparison.OrdinalIgnoreCase));

    /// <summary>The item ids a browser's after-exit clean covers: the monitor's
    /// explicit selection when configured, otherwise every non-destructive catalog
    /// item for that browser's family (the safe "caches and similar" default).
    /// Destructive items (cookies, history, passwords, ...) are never included
    /// automatically - auto-cleaning user data without the user present is not
    /// something a background pass may decide on its own. Unknown ids (items the
    /// catalog no longer offers) are ignored; an explicit empty set means the
    /// exit-clean does nothing for that browser.</summary>
    public IReadOnlySet<string> EffectiveExitItems(string browser)
    {
        var browserDefinition = BrowserCatalog.Find(browser);
        if (browserDefinition is null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var catalogItems = BrowserCatalog.ItemsFor(browserDefinition.Family);
        if (FindBrowserMonitor(browser)?.Items is { } chosen)
            // An explicit selection is honored as-is, including any destructive items
            // the user opted in to (they are unticked by default in the picker).
            // Project back to the catalog's canonical ids: BrowserCleanupService
            // matches item ids with ordinal comparisons, so a stored "CACHE" must
            // come out as "cache" or it would silently clean nothing.
            return catalogItems
                .Where(item => chosen.Any(id => id.Equals(item.Id, StringComparison.OrdinalIgnoreCase)))
                .Select(item => item.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // No explicit selection: the safe default excludes destructive items, so a
        // background clean never wipes user data unless it was deliberately enabled.
        return catalogItems
            .Where(item => !item.Destructive)
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CleanMachine", "settings.json");

    public static async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (File.Exists(FilePath))
            {
                await using var stream = File.OpenRead(FilePath);
                return await JsonSerializer.DeserializeAsync<AppSettings>(stream, cancellationToken: cancellationToken)
                    ?? new AppSettings();
            }
        }
        catch (IOException) { }
        catch (JsonException) { }
        return new AppSettings();
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        // Instant-save handlers fire concurrently (every toggle/keystroke), so writes
        // are serialized: two simultaneous saves would otherwise collide on one temp
        // file and throw IOException from an async-void handler (a hard crash).
        await SaveGate.WaitAsync(cancellationToken);
        var temporary = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, this,
                    new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
            File.Move(temporary, FilePath, true);
        }
        finally
        {
            SaveGate.Release();
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static readonly SemaphoreSlim SaveGate = new(1, 1);
}
