using System.Diagnostics;

namespace CleanMachine.Windows;

public sealed class BackgroundAgent : IDisposable
{
    private static readonly string[] BrowserProcessNames = ["chrome", "msedge", "firefox"];
    private readonly TimeSpan _pollInterval;
    private readonly Func<CancellationToken, Task> _onBrowserExit;
    private readonly PeriodicTimer _timer;
    private readonly CleanupService _cleanupService = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<int> _knownBrowserProcesses = [];
    public bool IsRunning { get; private set; }
    public BackgroundAgent(TimeSpan? pollInterval = null, Func<CancellationToken, Task>? onBrowserExit = null) { _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5); _onBrowserExit = onBrowserExit ?? (token => _cleanupService.CleanSelectedBrowsersAsync(token)); _timer = new PeriodicTimer(_pollInterval); }
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token); IsRunning = true;
        try
        {
            while (await _timer.WaitForNextTickAsync(linked.Token))
            {
                var current = new HashSet<int>();
                foreach (var process in Process.GetProcesses()) { try { if (BrowserProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase)) current.Add(process.Id); } catch { } finally { process.Dispose(); } }
                var browserExited = _knownBrowserProcesses.Count > 0 && !_knownBrowserProcesses.Overlaps(current);
                _knownBrowserProcesses.Clear(); _knownBrowserProcesses.UnionWith(current);
                if (browserExited) await _onBrowserExit(linked.Token);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally { IsRunning = false; }
    }
    public void Dispose() { _shutdown.Cancel(); _timer.Dispose(); _shutdown.Dispose(); }
}
