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
    public WipeMethod SecureDeleteMethod { get; set; } = WipeMethod.SimpleZeroFill;
    public int CustomWipePasses { get; set; } = 1;
    public HashSet<string> ProtectedBrowsers { get; set; } = ["chrome", "edge", "firefox"];
    public HashSet<string> ExcludedPaths { get; set; } = [];
    // Per-item Windows cleanup enable/disable overrides. A category is enabled when it is
    // listed here as enabled, or when it is enabled by default and not explicitly disabled.
    public HashSet<string> DisabledCleanupCategories { get; set; } = [];
    public HashSet<string> EnabledCleanupCategories { get; set; } = [];

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
