using System.Text.Json;

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
/// this browser and which action it takes.</summary>
public sealed class BrowserMonitorSetting
{
    public string Browser { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public ExitAction AfterExit { get; set; } = ExitAction.CleanAndNotify;
}

public sealed class AppSettings
{
    public bool BackgroundAgentEnabled { get; set; } = true;
    // Master switch: no browser-exit cleanup runs when this is off, regardless
    // of the per-browser entries below.
    public bool CleanOnBrowserExit { get; set; } = true;
    public bool CheckForUpdatesAutomatically { get; set; } = true;
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
    public ExitAction SystemMonitorAction { get; set; } = ExitAction.CleanSilently;

    // User-defined cleanup schedules, executed by Windows Task Scheduler so they run
    // even when the app is closed. An optional action (shutdown/restart/sleep) can
    // follow a successful run.
    public List<CleanupSchedule> Schedules { get; set; } = [];

    // Track whether we've created the desktop shortcut on first launch.
    public bool DesktopShortcutCreated { get; set; }

    public BrowserMonitorSetting? FindBrowserMonitor(string browser) =>
        BrowserMonitors.FirstOrDefault(m => m.Browser.Equals(browser, StringComparison.OrdinalIgnoreCase));

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
        var temporary = FilePath + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, this,
                new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
        File.Move(temporary, FilePath, true);
    }
}
