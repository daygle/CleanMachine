using System.Diagnostics;

namespace CleanMachine.Windows;

public sealed class BrowserCleanupService
{
    private readonly CleanupService _cleanup = new();
    public Task<IReadOnlyList<BrowserCleanupTarget>> ScanAsync(IEnumerable<string> browsers, CancellationToken token = default) => _cleanup.ScanBrowsersAsync(browsers, token);

    public async Task<CleanupReport> CleanWithReportAsync(IEnumerable<BrowserCleanupTarget> targets, bool requireBrowsersClosed = true, IProgress<CleanupProgress>? progress = null, CancellationToken token = default)
    {
        if (requireBrowsersClosed)
        {
            var running = GetRunningBrowsers();
            if (running.Count > 0) throw new InvalidOperationException($"Close these browsers before cleaning: {string.Join(", ", running.Distinct(StringComparer.OrdinalIgnoreCase))}.");
        }
        return await _cleanup.CleanBrowserTargetsAsync(targets, progress, token);
    }

    public async Task<CleanupResult> CleanAsync(IEnumerable<BrowserCleanupTarget> targets, bool requireBrowsersClosed = true, CancellationToken token = default)
        => (await CleanWithReportAsync(targets, requireBrowsersClosed, null, token)).Result;

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
}
