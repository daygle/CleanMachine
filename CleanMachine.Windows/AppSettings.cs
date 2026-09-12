using System.Text.Json;

namespace CleanMachine.Windows;

public sealed class AppSettings
{
    public bool BackgroundAgentEnabled { get; set; } = true;
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
