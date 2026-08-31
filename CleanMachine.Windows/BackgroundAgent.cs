using System.Diagnostics;

namespace CleanMachine.Windows;

public sealed class BackgroundAgent : IDisposable
{
    private static readonly string[] BrowserProcessNames = ["chrome", "msedge", "firefox"];
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(5));
    private readonly CleanupService _cleanupService = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<int> _knownBrowserProcesses = [];

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        while (await _timer.WaitForNextTickAsync(linked.Token))
        {
            var current = Process.GetProcesses().Where(IsBrowser).Select(process => process.Id).ToHashSet();
            var browserExited = _knownBrowserProcesses.Count > 0 && !_knownBrowserProcesses.Overlaps(current);
            _knownBrowserProcesses.Clear();
            _knownBrowserProcesses.UnionWith(current);

            if (browserExited)
            {
                // Cleanup is intentionally opt-in/config-driven. No files are removed by this foundation.
                await _cleanupService.CleanSelectedBrowsersAsync(linked.Token);
            }
        }
    }

    private static bool IsBrowser(Process process) => BrowserProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase);
    public void Dispose() { _shutdown.Cancel(); _timer.Dispose(); _shutdown.Dispose(); }
}
