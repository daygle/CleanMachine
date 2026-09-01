using System.Diagnostics;
using System.Text.Json;

namespace CleanMachine.Windows;

public sealed record BrowserCleanupOptions(
    IReadOnlySet<string>? ExcludedPaths = null,
    IReadOnlyList<string>? AdditionalProfileRoots = null,
    bool RequireBrowsersClosed = true);

public sealed record BrowserCleanupState(
    string OperationId,
    IReadOnlyList<string> RemainingFiles,
    int Removed,
    long BytesRecovered,
    DateTimeOffset UpdatedAt);

public sealed class BrowserCleanupService
{
    private readonly CleanupService _cleanup = new();
    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CleanMachine", "browser-cleanup-state.json");

    public Task<IReadOnlyList<BrowserCleanupTarget>> ScanAsync(
        IEnumerable<string> browsers,
        CancellationToken token = default)
        => _cleanup.ScanBrowsersAsync(browsers, token: token);

    public Task<IReadOnlyList<BrowserCleanupTarget>> ScanAsync(
        IEnumerable<string> browsers,
        IEnumerable<string>? additionalRoots = null,
        IReadOnlySet<string>? excludedPaths = null,
        CancellationToken token = default)
        => _cleanup.ScanBrowsersAsync(browsers, additionalRoots, excludedPaths, token);

    public async Task<CleanupReport> CleanWithReportAsync(
        IEnumerable<BrowserCleanupTarget> targets,
        BrowserCleanupOptions? options = null,
        IProgress<CleanupProgress>? progress = null,
        CancellationToken token = default)
    {
        options ??= new BrowserCleanupOptions();
        if (options.RequireBrowsersClosed)
        {
            var running = GetRunningBrowsers();
            if (running.Count > 0)
                throw new InvalidOperationException(
                    $"Close these browsers before cleaning: {string.Join(", ", running)}.");
        }

        var allowed = targets
            .Where(t => t.Selected && !IsExcluded(t.Path, options.ExcludedPaths))
            .ToArray();

        var operationId = Guid.NewGuid().ToString("N");
        var files = allowed
            .SelectMany(t => { try { return Directory.EnumerateFiles(t.Path, "*", SearchOption.AllDirectories); } catch { return []; } })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await SaveInterruptedStateAsync(
            new BrowserCleanupState(operationId, files, 0, 0, DateTimeOffset.UtcNow), token);

        var report = await _cleanup.CleanBrowserTargetsAsync(allowed, progress, token);
        await ClearStateAsync(token);
        return report;
    }

    public async Task<CleanupResult> CleanAsync(
        IEnumerable<BrowserCleanupTarget> targets,
        bool requireBrowsersClosed = true,
        CancellationToken token = default)
        => (await CleanWithReportAsync(targets, new BrowserCleanupOptions(RequireBrowsersClosed: requireBrowsersClosed), null, token)).Result;

    public async Task<BrowserCleanupState?> LoadInterruptedStateAsync(CancellationToken token = default)
    {
        try
        {
            if (!File.Exists(_statePath)) return null;
            await using var stream = File.OpenRead(_statePath);
            return await JsonSerializer.DeserializeAsync<BrowserCleanupState>(stream, cancellationToken: token);
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    public async Task SaveInterruptedStateAsync(BrowserCleanupState state, CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temp = _statePath + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, state with { UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken: token);
        File.Move(temp, _statePath, true);
    }

    public async Task ClearStateAsync(CancellationToken token = default)
    {
        await Task.CompletedTask;
        try { if (File.Exists(_statePath)) File.Delete(_statePath); } catch (IOException) { }
    }

    public static IReadOnlyList<string> GetRunningBrowsers()
    {
        var result = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            try { if (process.ProcessName is "chrome" or "msedge" or "firefox") result.Add(process.ProcessName); }
            catch { }
            finally { process.Dispose(); }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsExcluded(string path, IReadOnlySet<string>? exclusions)
        => exclusions?.Any(root => NativeSafety.IsWithin(path, root)) == true;
}
